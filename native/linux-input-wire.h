/* Private pipe framing shared by the Linux input helpers. Values match Xas.Core.InputWire. */
#ifndef XAS_LINUX_INPUT_WIRE_H
#define XAS_LINUX_INPUT_WIRE_H

#include <errno.h>
#include <stdint.h>
#include <stddef.h>
#include <unistd.h>

#define XAS_INPUT_EVENT_BYTES 12
#define XAS_INPUT_MAX_EVENTS 1024

static int xas_read_exact(int fd, unsigned char *data, size_t length) {
    size_t done = 0;
    while (done < length) {
        ssize_t n = read(fd, data + done, length - done);
        if (n == 0) return done == 0 ? 0 : -1;
        if (n < 0) { if (errno == EINTR) continue; return -1; }
        done += (size_t)n;
    }
    return 1;
}

static uint16_t xas_u16(const unsigned char *p) {
    return (uint16_t)(((uint16_t)p[0] << 8) | p[1]);
}

static int32_t xas_i32(const unsigned char *p) {
    uint32_t u = ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16)
               | ((uint32_t)p[2] << 8) | p[3];
    return (int32_t)u;
}

/* Returns 0 on EOF, -1 on malformed input/error, 1 on a complete frame. */
static int xas_read_frame(unsigned char *events, unsigned *count) {
    unsigned char header[2];
    int status = xas_read_exact(STDIN_FILENO, header, sizeof header);
    if (status != 1) return status;
    *count = xas_u16(header);
    if (*count > XAS_INPUT_MAX_EVENTS) return -1;
    if (*count == 0) return 1;
    return xas_read_exact(STDIN_FILENO, events, *count * XAS_INPUT_EVENT_BYTES);
}

#endif
