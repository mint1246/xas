#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pty.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

#define MAX_FRAME 65536u
#define OUT_CHUNK 16384u
#define INPUT_CAP (MAX_FRAME * 2u + 5u)
#define WRITE_CAP (1024u * 1024u)

static int write_all(int fd, const void *data, size_t len) {
    const unsigned char *p = data;
    while (len) {
        ssize_t n = write(fd, p, len);
        if (n > 0) { p += (size_t)n; len -= (size_t)n; continue; }
        if (n < 0 && errno == EINTR) continue;
        return -1;
    }
    return 0;
}

static int send_frame(uint8_t type, const void *payload, uint32_t len) {
    unsigned char header[5] = { type, (unsigned char)(len >> 24),
        (unsigned char)(len >> 16), (unsigned char)(len >> 8), (unsigned char)len };
    if (write_all(STDOUT_FILENO, header, sizeof header) < 0) return -1;
    return len ? write_all(STDOUT_FILENO, payload, len) : 0;
}

static void send_error(const char *message) {
    size_t n = strlen(message);
    if (n > MAX_FRAME) n = MAX_FRAME;
    (void)send_frame(0xff, message, (uint32_t)n);
}

static int parse_uint(const char *s, unsigned long min, unsigned long max, unsigned long *out) {
    char *end = NULL;
    errno = 0;
    unsigned long v = strtoul(s, &end, 10);
    if (errno || !*s || !end || *end || v < min || v > max) return -1;
    *out = v;
    return 0;
}

static int exit_status(int status) {
    if (WIFEXITED(status)) return WEXITSTATUS(status);
    if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
    return 1;
}

static int terminate_group(pid_t child) {
    int status;
    (void)kill(-child, SIGTERM);
    for (int i = 0; i < 20; ++i) {
        pid_t r = waitpid(child, &status, WNOHANG);
        if (r == child) return status;
        if (r < 0 && errno == ECHILD) return 0;
        struct timespec ts = { .tv_sec = 0, .tv_nsec = 50000000 };
        (void)nanosleep(&ts, NULL);
    }
    (void)kill(-child, SIGKILL);
    pid_t r;
    do { r = waitpid(child, &status, 0); } while (r < 0 && errno == EINTR);
    return r == child ? status : 0;
}

static int queue_bytes(unsigned char *queue, size_t *used, const unsigned char *data, size_t n) {
    if (n > WRITE_CAP - *used) return -1;
    memcpy(queue + *used, data, n);
    *used += n;
    return 0;
}

int main(int argc, char **argv) {
    const char *shell = NULL;
    unsigned long cols = 80, rows = 24;
    for (int i = 1; i < argc; ++i) {
        if (!strcmp(argv[i], "--shell") && i + 1 < argc) shell = argv[++i];
        else if (!strcmp(argv[i], "--cols") && i + 1 < argc) {
            if (parse_uint(argv[++i], 1, 65535, &cols) < 0) { fprintf(stderr, "invalid --cols\n"); return 2; }
        } else if (!strcmp(argv[i], "--rows") && i + 1 < argc) {
            if (parse_uint(argv[++i], 1, 65535, &rows) < 0) { fprintf(stderr, "invalid --rows\n"); return 2; }
        } else { fprintf(stderr, "usage: %s [--shell PATH] [--cols N] [--rows N]\n", argv[0]); return 2; }
    }
    if (!shell || !*shell) shell = getenv("SHELL");
    if (!shell || !*shell) shell = "/bin/sh";
    if (strlen(shell) > 4096) { fprintf(stderr, "shell path too long\n"); return 2; }

    struct winsize ws = { .ws_row = (unsigned short)rows, .ws_col = (unsigned short)cols };
    int master = -1;
    signal(SIGPIPE, SIG_IGN);
    pid_t child = forkpty(&master, NULL, NULL, &ws);
    if (child < 0) { send_error("forkpty failed"); return 1; }
    if (child == 0) {
        signal(SIGPIPE, SIG_DFL);
        const char *base = strrchr(shell, '/');
        base = base ? base + 1 : shell;
        size_t n = strlen(base);
        char *login_name = malloc(n + 2);
        if (!login_name) _exit(127);
        login_name[0] = '-';
        memcpy(login_name + 1, base, n + 1);
        char *const child_argv[] = { login_name, NULL };
        execvp(shell, child_argv);
        dprintf(STDERR_FILENO, "exec %s failed: %s\n", shell, strerror(errno));
        _exit(127);
    }

    int flags = fcntl(master, F_GETFL, 0);
    if (flags >= 0) (void)fcntl(master, F_SETFL, flags | O_NONBLOCK);
    unsigned char incoming[INPUT_CAP];
    size_t incoming_len = 0;
    unsigned char *to_child = malloc(WRITE_CAP);
    if (!to_child) {
        (void)terminate_group(child);
        close(master);
        send_error("out of memory");
        return 1;
    }
    size_t queued = 0;
    int input_open = 1, master_open = 1, reaped = 0, forced = 0;
    int child_status = 0;

    while (1) {
        if (!reaped) {
            pid_t wr = waitpid(child, &child_status, WNOHANG);
            if (wr == child) reaped = 1;
            else if (wr < 0 && errno == ECHILD) { reaped = 1; child_status = 0; }
        }
        if (forced) {
            if (!reaped) child_status = terminate_group(child);
            reaped = 1;
            master_open = 0;
            break;
        }
        if (reaped && !master_open) break;

        struct pollfd pfds[2];
        nfds_t count = 0;
        int input_index = -1, master_index = -1;
        if (input_open && queued <= WRITE_CAP - MAX_FRAME) {
            input_index = (int)count;
            pfds[count++] = (struct pollfd){ .fd = STDIN_FILENO, .events = POLLIN | POLLHUP };
        }
        if (master_open) {
            master_index = (int)count;
            short events = POLLIN | POLLHUP;
            if (queued) events |= POLLOUT;
            pfds[count++] = (struct pollfd){ .fd = master, .events = events };
        }
        int pr = poll(pfds, count, 100);
        if (pr < 0 && errno != EINTR) { forced = 1; continue; }

        if (input_index >= 0 && (pfds[input_index].revents & (POLLIN | POLLHUP | POLLERR))) {
            if (incoming_len == sizeof incoming) { send_error("input frame buffer overflow"); forced = 1; continue; }
            ssize_t n = read(STDIN_FILENO, incoming + incoming_len, sizeof incoming - incoming_len);
            if (n == 0) { input_open = 0; forced = 1; continue; }
            if (n < 0 && errno != EINTR && errno != EAGAIN) { input_open = 0; forced = 1; continue; }
            if (n > 0) incoming_len += (size_t)n;
        }

        while (incoming_len >= 5) {
            uint32_t len = ((uint32_t)incoming[1] << 24) | ((uint32_t)incoming[2] << 16) |
                ((uint32_t)incoming[3] << 8) | incoming[4];
            if (len > MAX_FRAME) { send_error("frame exceeds maximum payload"); forced = 1; break; }
            if (incoming_len < (size_t)len + 5) break;
            const unsigned char *payload = incoming + 5;
            if (incoming[0] == 0x01 && len > 0) {
                if (queue_bytes(to_child, &queued, payload, len) < 0) { send_error("PTY input queue full"); forced = 1; break; }
            } else if (incoming[0] == 0x02 && len == 4) {
                unsigned int new_cols = ((unsigned int)payload[0] << 8) | payload[1];
                unsigned int new_rows = ((unsigned int)payload[2] << 8) | payload[3];
                if (!new_cols || !new_rows) { send_error("resize dimensions must be nonzero"); forced = 1; break; }
                struct winsize size = { .ws_col = (unsigned short)new_cols, .ws_row = (unsigned short)new_rows };
                if (ioctl(master, TIOCSWINSZ, &size) < 0) { send_error("PTY resize failed"); forced = 1; break; }
            } else if (incoming[0] == 0x03 && len == 0) {
                forced = 1;
                break;
            } else {
                send_error("unknown frame type or invalid frame length");
                forced = 1;
                break;
            }
            size_t consumed = (size_t)len + 5;
            memmove(incoming, incoming + consumed, incoming_len - consumed);
            incoming_len -= consumed;
        }

        if (master_index >= 0 && (pfds[master_index].revents & POLLOUT) && queued) {
            ssize_t n = write(master, to_child, queued);
            if (n > 0) { queued -= (size_t)n; memmove(to_child, to_child + n, queued); }
            else if (n < 0 && errno != EINTR && errno != EAGAIN && errno != EIO) { forced = 1; continue; }
        }
        if (master_index >= 0 && (pfds[master_index].revents & (POLLIN | POLLHUP | POLLERR))) {
            unsigned char out[OUT_CHUNK];
            ssize_t n = read(master, out, sizeof out);
            if (n > 0) {
                if (send_frame(0x81, out, (uint32_t)n) < 0) { forced = 1; continue; }
            } else if ((n == 0) || (n < 0 && errno == EIO)) {
                master_open = 0;
                close(master);
            } else if (n < 0 && errno != EINTR && errno != EAGAIN) {
                master_open = 0;
                close(master);
            }
        }
    }

    if (master_open) close(master);
    free(to_child);
    int code = exit_status(child_status);
    unsigned char result[4] = { (unsigned char)((uint32_t)code >> 24), (unsigned char)((uint32_t)code >> 16),
        (unsigned char)((uint32_t)code >> 8), (unsigned char)code };
    (void)send_frame(0x82, result, sizeof result);
    return code;
}
