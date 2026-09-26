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

static int tablet_fd = -1, mouse_fd = -1, keyboard_fd = -1;
static int abs_max_x = DEFAULT_WIDTH - 1, abs_max_y = DEFAULT_HEIGHT - 1;
static int tool_in_proximity = 0;
static unsigned char held_keys[768], held_buttons[9];

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

static int setup_tablet(int width, int height, int physical_width_mm, int physical_height_mm) {
    int fd = create_device("XAS Remote Tablet");
    if (fd < 0) return -1;

    /* Linux input's tablet guidelines require both DIRECT and POINTER for new tablet devices. */
    prop(fd, INPUT_PROP_DIRECT);
    prop(fd, INPUT_PROP_POINTER);

    bit(fd, UI_SET_EVBIT, EV_KEY);
    bit(fd, UI_SET_EVBIT, EV_ABS);
    bit(fd, UI_SET_EVBIT, EV_SYN);

    bit(fd, UI_SET_KEYBIT, BTN_TOOL_PEN);
    bit(fd, UI_SET_KEYBIT, BTN_STYLUS);

    int resolution_x = physical_width_mm > 0 ? width / physical_width_mm : 4;
    int resolution_y = physical_height_mm > 0 ? height / physical_height_mm : 4;
    if (resolution_x < 1) resolution_x = 1;
    if (resolution_y < 1) resolution_y = 1;
    struct uinput_abs_setup abs_x = { .code = ABS_X, .absinfo = {
        .minimum = 0, .maximum = width - 1, .resolution = resolution_x } };
    struct uinput_abs_setup abs_y = { .code = ABS_Y, .absinfo = {
        .minimum = 0, .maximum = height - 1, .resolution = resolution_y } };
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

static int setup_mouse(void) {
    int fd = create_device("XAS Remote Mouse");
    if (fd < 0) return -1;
    prop(fd, INPUT_PROP_POINTER);
    bit(fd, UI_SET_EVBIT, EV_KEY);
    bit(fd, UI_SET_EVBIT, EV_REL);
    bit(fd, UI_SET_EVBIT, EV_SYN);
    bit(fd, UI_SET_KEYBIT, BTN_LEFT);
    bit(fd, UI_SET_KEYBIT, BTN_RIGHT);
    bit(fd, UI_SET_KEYBIT, BTN_MIDDLE);
    bit(fd, UI_SET_KEYBIT, BTN_SIDE);
    bit(fd, UI_SET_KEYBIT, BTN_EXTRA);
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
    for (unsigned i = 1; i < 8; i++)
        if (held_buttons[i]) { emit(mouse_fd, EV_KEY, button_evdev((int)i), 0); held_buttons[i] = 0; }
    if (tool_in_proximity) {
        emit(tablet_fd, EV_KEY, BTN_TOOL_PEN, 0);
        tool_in_proximity = 0;
    }
    commit(tablet_fd);
    commit(mouse_fd);
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
        if (!tool_in_proximity) {
            emit(tablet_fd, EV_KEY, BTN_TOOL_PEN, 1);
            tool_in_proximity = 1;
        }
        emit(tablet_fd, EV_ABS, ABS_X, x);
        emit(tablet_fd, EV_ABS, ABS_Y, y);
    } else if (kind == 1) {
        emit(mouse_fd, EV_REL, REL_X, x);
        emit(mouse_fd, EV_REL, REL_Y, y);
    } else if (kind == 2) {
        int b = button_code((int)code);
        if (!b) return 0;
        emit(mouse_fd, EV_KEY, b, down != 0);
        if (down) held_buttons[code & 7] = 1; else held_buttons[code & 7] = 0;
    } else if (kind == 4) {
        uint32_t key = xas_hid_key((uint16_t)code);
        if (!key || key >= sizeof held_keys) return -1;
        if (repeat) { emit(keyboard_fd, EV_KEY, key, 1); emit(keyboard_fd, EV_KEY, key, 2); }
        else emit(keyboard_fd, EV_KEY, key, down != 0);
        if (down || repeat) held_keys[key] = 1; else held_keys[key] = 0;
    } else if (kind == 3) {
        emit(mouse_fd, EV_REL, REL_WHEEL, -y);
        if (x) emit(mouse_fd, EV_REL, REL_HWHEEL, x);
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

    int physical_width_mm = argc > 3 ? atoi(argv[3]) : 0;
    int physical_height_mm = argc > 4 ? atoi(argv[4]) : 0;
    tablet_fd = setup_tablet(width, height, physical_width_mm, physical_height_mm);
    if (tablet_fd < 0) return 1;
    mouse_fd = setup_mouse();
    if (mouse_fd < 0) { ioctl(tablet_fd, UI_DEV_DESTROY); close(tablet_fd); return 1; }
    keyboard_fd = setup_keyboard();
    if (keyboard_fd < 0) {
        ioctl(tablet_fd, UI_DEV_DESTROY); ioctl(mouse_fd, UI_DEV_DESTROY);
        close(tablet_fd); close(mouse_fd); return 1;
    }

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
        commit(tablet_fd);
        commit(mouse_fd);
        commit(keyboard_fd);
    }
    free(events);
    release_all();
    ioctl(tablet_fd, UI_DEV_DESTROY);
    ioctl(mouse_fd, UI_DEV_DESTROY);
    ioctl(keyboard_fd, UI_DEV_DESTROY);
    close(tablet_fd);
    close(mouse_fd);
    close(keyboard_fd);
    return 0;
}

