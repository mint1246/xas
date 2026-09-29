/*
 * XAS virtual input device for Linux.
 *
 * Creates a uinput device pair so input can be injected on any session, X11 or Wayland, with no portal
 * and therefore no per-session consent prompt. The pointer is advertised as INPUT_PROP_DIRECT with an
 * absolute axis range, which makes the compositor map it to the screen one-to-one instead of running it
 * through pointer acceleration; that is what keeps remote motion feeling like the physical mouse.
 *
 * Stdin uses a 2-byte big-endian event count followed by 12-byte InputWire records.
 * A zero count releases everything held. Keyboard codes are USB HID usages mapped to evdev here.
 */

#define _POSIX_C_SOURCE 200809L

#include <errno.h>
#include <fcntl.h>
#include <linux/uinput.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <time.h>
#include <unistd.h>
#include "../linux-hid-key.h"
#include "../linux-input-wire.h"

#define XAS_VENDOR 0x7861
#define XAS_PRODUCT 0x0001
#define DEFAULT_WIDTH 1920
#define DEFAULT_HEIGHT 1080

static int absolute_fd = -1, relative_fd = -1, keyboard_fd = -1;
static int abs_max_x = DEFAULT_WIDTH - 1, abs_max_y = DEFAULT_HEIGHT - 1;
static int last_pointer_fd = -1;
static unsigned char held_keys[768], held_buttons[9];
static int held_button_fd[9];

static void emit(int fd, int type, int code, int value) {
    struct input_event ev = { .type = (unsigned short)type, .code = (unsigned short)code, .value = value };
    if (write(fd, &ev, sizeof ev) != (ssize_t)sizeof ev) {
        fprintf(stderr, "uinput write failed: %s\n", strerror(errno));
        exit(3);
    }
}

/* Named commit() because unistd.h already declares a sync(void). */
static void commit(int fd) { emit(fd, EV_SYN, SYN_REPORT, 0); }

static int button_code(int n) {
    switch (n) { case 1: return BTN_LEFT; case 2: return BTN_RIGHT; case 3: return BTN_MIDDLE;
        case 4: return BTN_SIDE; case 5: return BTN_EXTRA; default: return 0; }
}

/* BTN_LEFT and BTN_RIGHT sit either side of BTN_MIDDLE rather than being contiguous. */
static int button_evdev(int n) { return n == 1 ? BTN_LEFT : n == 2 ? BTN_RIGHT : button_code(n); }

/* Enables one control bit, tolerating EEXIST so overlapping sets are harmless. */
static void bit(int fd, int type, int code) {
    if (ioctl(fd, type, code) < 0 && errno != EEXIST) {
        fprintf(stderr, "uinput could not enable %d/%d: %s\n", type, code, strerror(errno));
        exit(3);
    }
}

static int create_device(const char *name) {
    int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
    if (fd < 0) {
        fprintf(stderr, "cannot open /dev/uinput: %s\n", strerror(errno));
        fprintf(stderr, "grant access with a udev rule, for example:\n");
        fprintf(stderr, "  echo 'KERNEL==\"uinput\", MODE=\"0660\", GROUP=\"input\", OPTIONS+=\"static_node=uinput\"' "
                        "| sudo tee /etc/udev/rules.d/99-xas-uinput.rules\n");
        fprintf(stderr, "then: sudo udevadm control --reload-rules && sudo udevadm trigger\n");
        return -1;
    }
    struct uinput_setup setup = { 0 };
    setup.id.bustype = BUS_USB;
    setup.id.vendor = XAS_VENDOR;
    setup.id.product = XAS_PRODUCT;
    setup.id.version = 1;
    setup.ff_effects_max = 0;
    /* uinput_setup.name is a fixed char array, so copy rather than assign a pointer. */
    memcpy(setup.name, name, sizeof setup.name - 1);
    if (ioctl(fd, UI_DEV_SETUP, &setup) < 0) {
        fprintf(stderr, "UI_DEV_SETUP failed: %s\n", strerror(errno));
        close(fd);
        return -1;
    }
    return fd;
}

static void prop(int fd, int code) {
    if (ioctl(fd, UI_SET_PROPBIT, code) < 0 && errno != EEXIST) {
        fprintf(stderr, "uinput could not enable property %d: %s\n", code, strerror(errno));
        exit(3);
    }
}

static void enable_mouse_buttons(int fd) {
    bit(fd, UI_SET_EVBIT, EV_KEY);
    bit(fd, UI_SET_KEYBIT, BTN_LEFT);
    bit(fd, UI_SET_KEYBIT, BTN_RIGHT);
    bit(fd, UI_SET_KEYBIT, BTN_MIDDLE);
    bit(fd, UI_SET_KEYBIT, BTN_SIDE);
    bit(fd, UI_SET_KEYBIT, BTN_EXTRA);
}

static int setup_absolute_mouse(int width, int height) {
    int fd = create_device("XAS Remote Absolute Mouse");
    if (fd < 0) return -1;

    /* Match Sunshine/libvirtualhid's proven absolute-mouse shape: absolute axes, buttons and
       INPUT_PROP_DIRECT, but no relative axes and no pen/tablet tool bits. */
    prop(fd, INPUT_PROP_DIRECT);
    enable_mouse_buttons(fd);
    bit(fd, UI_SET_EVBIT, EV_ABS);
    bit(fd, UI_SET_EVBIT, EV_SYN);
    struct uinput_abs_setup abs_x = { .code = ABS_X, .absinfo = { .minimum = 0, .maximum = width - 1 } };
    struct uinput_abs_setup abs_y = { .code = ABS_Y, .absinfo = { .minimum = 0, .maximum = height - 1 } };
    if (ioctl(fd, UI_ABS_SETUP, &abs_x) < 0 || ioctl(fd, UI_ABS_SETUP, &abs_y) < 0) {
        fprintf(stderr, "UI_ABS_SETUP failed: %s\n", strerror(errno));
        close(fd);
        return -1;
    }
    if (ioctl(fd, UI_DEV_CREATE) < 0) {
        fprintf(stderr, "UI_DEV_CREATE failed: %s\n", strerror(errno));
        close(fd);
        return -1;
    }
    abs_max_x = width - 1;
    abs_max_y = height - 1;
    return fd;
}

static int setup_relative_mouse(void) {
    int fd = create_device("XAS Remote Mouse");
    if (fd < 0) return -1;
    prop(fd, INPUT_PROP_POINTER);
    enable_mouse_buttons(fd);
    bit(fd, UI_SET_EVBIT, EV_REL);
    bit(fd, UI_SET_EVBIT, EV_SYN);
    bit(fd, UI_SET_RELBIT, REL_X);
    bit(fd, UI_SET_RELBIT, REL_Y);
    bit(fd, UI_SET_RELBIT, REL_WHEEL);
    bit(fd, UI_SET_RELBIT, REL_HWHEEL);
    if (ioctl(fd, UI_DEV_CREATE) < 0) {
        fprintf(stderr, "mouse UI_DEV_CREATE failed: %s\n", strerror(errno));
        close(fd);
        return -1;
    }
    return fd;
}

static int setup_keyboard(void) {
    int fd = create_device("XAS Remote Keyboard");
    if (fd < 0) return -1;
    bit(fd, UI_SET_EVBIT, EV_KEY);
    bit(fd, UI_SET_EVBIT, EV_SYN);

    for (unsigned usage = 4; usage <= 231; usage++) {
        uint32_t key = xas_hid_key((uint16_t)usage);
        if (key) bit(fd, UI_SET_KEYBIT, key);
    }
    if (ioctl(fd, UI_DEV_CREATE) < 0) {
        fprintf(stderr, "keyboard UI_DEV_CREATE failed: %s\n", strerror(errno));
        close(fd);
        return -1;
    }
    return fd;
}

static void release_all(void) {
    for (unsigned i = 0; i < sizeof held_keys; i++)
        if (held_keys[i]) { emit(keyboard_fd, EV_KEY, (int)i, 0); held_keys[i] = 0; }
    for (unsigned i = 1; i < 8; i++) {
        if (!held_buttons[i]) continue;
        int fd = held_button_fd[i] >= 0 ? held_button_fd[i] : relative_fd;
        emit(fd, EV_KEY, button_evdev((int)i), 0);
        held_buttons[i] = 0;
        held_button_fd[i] = -1;
    }
    commit(absolute_fd);
    commit(relative_fd);
    commit(keyboard_fd);
}

static int handle_event(const unsigned char *event) {
    unsigned kind = event[0]; unsigned code = xas_u16(event + 1);
    int flags = event[3], down = flags & 1, repeat = flags & 2;
    int x = xas_i32(event + 4), y = xas_i32(event + 8);
    if (kind == 0) return 0;
    if (kind == 5) {
        /* Clamp rather than reject: a stale coordinate must not stall the stream. */
        if (x < 0) x = 0;
        if (x > abs_max_x) x = abs_max_x;
        if (y < 0) y = 0;
        if (y > abs_max_y) y = abs_max_y;
        emit(absolute_fd, EV_ABS, ABS_X, x);
        emit(absolute_fd, EV_ABS, ABS_Y, y);
        last_pointer_fd = absolute_fd;
    } else if (kind == 1) {
        emit(relative_fd, EV_REL, REL_X, x);
        emit(relative_fd, EV_REL, REL_Y, y);
        last_pointer_fd = relative_fd;
    } else if (kind == 2) {
        int b = button_code((int)code);
        if (!b) return 0;
        unsigned index = code & 7;
        int fd;
        if (down) {
            fd = last_pointer_fd >= 0 ? last_pointer_fd : relative_fd;
            held_buttons[index] = 1;
            held_button_fd[index] = fd;
        } else {
            fd = held_button_fd[index] >= 0 ? held_button_fd[index]
                : (last_pointer_fd >= 0 ? last_pointer_fd : relative_fd);
            held_buttons[index] = 0;
            held_button_fd[index] = -1;
        }
        emit(fd, EV_KEY, b, down != 0);
    } else if (kind == 4) {
        uint32_t key = xas_hid_key((uint16_t)code);
        if (!key || key >= sizeof held_keys) return -1;
        if (repeat) { emit(keyboard_fd, EV_KEY, key, 1); emit(keyboard_fd, EV_KEY, key, 2); }
        else emit(keyboard_fd, EV_KEY, key, down != 0);
        if (down || repeat) held_keys[key] = 1; else held_keys[key] = 0;
    } else if (kind == 3) {
        /* Both Windows/XAS and evdev use positive vertical wheel values for up. */
        emit(relative_fd, EV_REL, REL_WHEEL, y);
        if (x) emit(relative_fd, EV_REL, REL_HWHEEL, x);
    } else return -1;

    return 0;
}

int main(int argc, char **argv) {
    if (argc == 2 && strcmp(argv[1], "--probe") == 0) {
        int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
        if (fd < 0) return 1;
        close(fd);
        return 0;
    }
    int width = argc > 1 ? atoi(argv[1]) : DEFAULT_WIDTH;
    int height = argc > 2 ? atoi(argv[2]) : DEFAULT_HEIGHT;
    if (width < 1 || height < 1) { width = DEFAULT_WIDTH; height = DEFAULT_HEIGHT; }

    absolute_fd = setup_absolute_mouse(width, height);
    if (absolute_fd < 0) return 1;
    relative_fd = setup_relative_mouse();
    if (relative_fd < 0) { ioctl(absolute_fd, UI_DEV_DESTROY); close(absolute_fd); return 1; }
    keyboard_fd = setup_keyboard();
    if (keyboard_fd < 0) {
        ioctl(absolute_fd, UI_DEV_DESTROY); ioctl(relative_fd, UI_DEV_DESTROY);
        close(absolute_fd); close(relative_fd); return 1;
    }
    for (unsigned i = 0; i < sizeof held_button_fd / sizeof held_button_fd[0]; i++) held_button_fd[i] = -1;
    last_pointer_fd = relative_fd;

    /* Give udev and libinput a moment to notice the new devices before events start arriving. */
    struct timespec settle = { .tv_sec = 0, .tv_nsec = 400 * 1000 * 1000 };
    while (nanosleep(&settle, &settle) < 0 && errno == EINTR) { }
    puts("READY");
    fflush(stdout);

    unsigned char *events = malloc(XAS_INPUT_MAX_EVENTS * XAS_INPUT_EVENT_BYTES);
    if (!events) return 1;
    for (;;) {
        unsigned count = 0;
        int status = xas_read_frame(events, &count);
        if (status <= 0) break;
        if (count == 0) { release_all(); continue; }
        for (unsigned i = 0; i < count; i++) if (handle_event(events + i * XAS_INPUT_EVENT_BYTES) < 0) fprintf(stderr, "unsupported input event in batch\n");
        commit(absolute_fd);
        commit(relative_fd);
        commit(keyboard_fd);
    }
    free(events);
    release_all();
    ioctl(absolute_fd, UI_DEV_DESTROY);
    ioctl(relative_fd, UI_DEV_DESTROY);
    ioctl(keyboard_fd, UI_DEV_DESTROY);
    close(absolute_fd);
    close(relative_fd);
    close(keyboard_fd);
    return 0;
}

