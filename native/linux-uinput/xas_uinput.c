/*
 * XAS virtual input device for Linux.
 *
 * Creates a uinput device pair so input can be injected on any session, X11 or Wayland, with no portal
 * and therefore no per-session consent prompt. The pointer is advertised as INPUT_PROP_DIRECT with an
 * absolute axis range, which makes the compositor map it to the screen one-to-one instead of running it
 * through pointer acceleration; that is what keeps remote motion feeling like the physical mouse.
 *
 * The line protocol on stdin is unchanged from the EIS helper, so the daemon's backend is nearly identical:
 *   M <dx> <dy>            relative motion
 *   A <x> <y>              absolute motion in display pixels
 *   B <button> <down>      button, 1 left, 2 right, 3 middle
 *   S <dx> <dy>            scroll
 *   K <code> <down> <rep>  key, using a Linux evdev code
 *   R                      release everything held
 *   N                      keepalive
 */

#include <errno.h>
#include <fcntl.h>
#include <linux/uinput.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

#define XAS_VENDOR 0x7861
#define XAS_PRODUCT 0x0001
#define DEFAULT_WIDTH 1920
#define DEFAULT_HEIGHT 1080

static int pointer_fd = -1, keyboard_fd = -1;
static int abs_max_x = DEFAULT_WIDTH - 1, abs_max_y = DEFAULT_HEIGHT - 1;
static unsigned char held_keys[768], held_buttons[8];

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

static int create_device(const char *name, int props) {
    int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
    if (fd < 0) {
        fprintf(stderr, "cannot open /dev/uinput: %s\n", strerror(errno));
        fprintf(stderr, "grant access with a udev rule, for example:\n");
        fprintf(stderr, "  echo 'KERNEL==\"uinput\", MODE=\"0660\", GROUP=\"input\", OPTIONS+=\"static_node=uinput\"' "
                        "| sudo tee /etc/udev/rules.d/99-xas-uinput.rules\n");
        fprintf(stderr, "then: sudo udevadm control --reload-rules && sudo udevadm trigger\n");
        return -1;
    }
    if (ioctl(fd, UI_SET_PROPBIT, props) < 0 && errno != EEXIST) { /* older kernels lack prop bits */ }
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

static int setup_pointer(int width, int height) {
    /* INPUT_PROP_DIRECT alone marks this as a tablet-like device whose absolute axes the compositor maps
       to the screen. Adding INPUT_PROP_POINTER would make libinput classify it as a mouse, and a mouse
       ignores the absolute axes entirely, so absolute motion would be silently dropped. */
    int fd = create_device("XAS Remote Pointer", INPUT_PROP_DIRECT);
    if (fd < 0) return -1;

    bit(fd, UI_SET_EVBIT, EV_KEY);
    bit(fd, UI_SET_EVBIT, EV_ABS);
    bit(fd, UI_SET_EVBIT, EV_REL);
    bit(fd, UI_SET_EVBIT, EV_SYN);

    bit(fd, UI_SET_KEYBIT, BTN_LEFT);
    bit(fd, UI_SET_KEYBIT, BTN_RIGHT);
    bit(fd, UI_SET_KEYBIT, BTN_MIDDLE);
    bit(fd, UI_SET_KEYBIT, BTN_TOOL_PEN);
    bit(fd, UI_SET_KEYBIT, BTN_STYLUS);
    bit(fd, UI_SET_KEYBIT, BTN_STYLUS2);

    bit(fd, UI_SET_RELBIT, REL_X);
    bit(fd, UI_SET_RELBIT, REL_Y);
    bit(fd, UI_SET_RELBIT, REL_WHEEL);
    bit(fd, UI_SET_RELBIT, REL_HWHEEL);

    /* The absolute range is the display, so the daemon can send display pixels unchanged. */
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

static int setup_keyboard(void) {
    int fd = create_device("XAS Remote Keyboard", 0);
    if (fd < 0) return -1;
    bit(fd, UI_SET_EVBIT, EV_KEY);
    bit(fd, UI_SET_EVBIT, EV_SYN);

    static const int keys[] = {
        KEY_ESC, KEY_ENTER, KEY_TAB, KEY_BACKSPACE, KEY_SPACE, KEY_DELETE, KEY_INSERT,
        KEY_HOME, KEY_END, KEY_PAGEUP, KEY_PAGEDOWN, KEY_LEFT, KEY_RIGHT, KEY_UP, KEY_DOWN,
        KEY_CAPSLOCK, KEY_LEFTCTRL, KEY_LEFTALT, KEY_LEFTMETA, KEY_RIGHTSHIFT, KEY_RIGHTCTRL,
        KEY_RIGHTALT, KEY_RIGHTMETA, KEY_MENU, KEY_COMMA, KEY_DOT, KEY_MINUS, KEY_EQUAL,
        KEY_SEMICOLON, KEY_APOSTROPHE, KEY_GRAVE, KEY_BACKSLASH, KEY_LEFTBRACE, KEY_RIGHTBRACE,
        KEY_SLASH, KEY_BACKSLASH, KEY_FN, KEY_MENU, KEY_PAUSE, KEY_SYSRQ, KEY_SCROLLLOCK,
        KEY_F1, KEY_F2, KEY_F3, KEY_F4, KEY_F5, KEY_F6, KEY_F7, KEY_F8, KEY_F9, KEY_F10, KEY_F11, KEY_F12
    };
    for (size_t i = 0; i < sizeof keys / sizeof keys[0]; i++) bit(fd, UI_SET_KEYBIT, keys[i]);
    for (int c = KEY_1; c <= KEY_0; c++) bit(fd, UI_SET_KEYBIT, c);      /* digits are not contiguous */
    for (int c = KEY_Q; c <= KEY_P; c++) bit(fd, UI_SET_KEYBIT, c);      /* top row */
    for (int c = KEY_A; c <= KEY_L; c++) bit(fd, UI_SET_KEYBIT, c);
    for (int c = KEY_Z; c <= KEY_M; c++) bit(fd, UI_SET_KEYBIT, c);
    for (int c = KEY_KP0; c <= KEY_KP9; c++) bit(fd, UI_SET_KEYBIT, c);
    for (int c = KEY_KPDOT; c <= KEY_KPENTER; c++) bit(fd, UI_SET_KEYBIT, c);
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
        if (held_buttons[i]) { emit(pointer_fd, EV_KEY, button_evdev((int)i), 0); held_buttons[i] = 0; }
    commit(pointer_fd);
    commit(keyboard_fd);
}

static int handle(char *line) {
    char op; int x = 0, y = 0, down = 0, repeat = 0; unsigned code = 0;
    if (sscanf(line, "%c", &op) != 1) return -1;
    /* Traces what actually arrives, which distinguishes "no events sent" from "events ignored". */
    if (getenv("XAS_UINPUT_DEBUG")) fprintf(stderr, "in: %s", line);

    if (op == 'R') { release_all(); return 0; }
    if (op == 'N') return 0;
    if (op == 'A' && sscanf(line, "A %d %d", &x, &y) == 2) {
        /* Clamp rather than reject: a stale coordinate must not stall the stream. */
        if (x < 0) x = 0;
        if (x > abs_max_x) x = abs_max_x;
        if (y < 0) y = 0;
        if (y > abs_max_y) y = abs_max_y;
        emit(pointer_fd, EV_ABS, ABS_X, x);
        emit(pointer_fd, EV_ABS, ABS_Y, y);
    } else if (op == 'M' && sscanf(line, "M %d %d", &x, &y) == 2) {
        emit(pointer_fd, EV_REL, REL_X, x);
        emit(pointer_fd, EV_REL, REL_Y, y);
    } else if (op == 'B' && sscanf(line, "B %u %d", &code, &down) == 2) {
        int b = button_code((int)code);
        if (!b) return 0;
        emit(pointer_fd, EV_KEY, b, down != 0);
        if (down) held_buttons[code & 7] = 1; else held_buttons[code & 7] = 0;
    } else if (op == 'K' && sscanf(line, "K %u %d %d", &code, &down, &repeat) == 3) {
        if (code >= sizeof held_keys) return 0;
        if (repeat) { emit(keyboard_fd, EV_KEY, (int)code, 1); emit(keyboard_fd, EV_KEY, (int)code, 2); }
        else emit(keyboard_fd, EV_KEY, (int)code, down != 0);
        if (!(down || repeat)) held_keys[code] = 0;
    } else if (op == 'S' && sscanf(line, "S %d %d", &x, &y) == 2) {
        emit(pointer_fd, EV_REL, REL_WHEEL, -y);
        if (x) emit(pointer_fd, EV_REL, REL_HWHEEL, x);
    } else return -1;

    commit(pointer_fd);
    commit(keyboard_fd);
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

    pointer_fd = setup_pointer(width, height);
    if (pointer_fd < 0) return 1;
    keyboard_fd = setup_keyboard();
    if (keyboard_fd < 0) { ioctl(pointer_fd, UI_DEV_DESTROY); return 1; }

    /* Give udev and libinput a moment to notice the new devices before events start arriving. */
    usleep(400 * 1000);
    puts("READY");
    fflush(stdout);

    char *line = NULL;
    size_t cap = 0;
    while (getline(&line, &cap, stdin) > 0) handle(line);
    free(line);
    release_all();
    ioctl(pointer_fd, UI_DEV_DESTROY);
    ioctl(keyboard_fd, UI_DEV_DESTROY);
    close(pointer_fd);
    close(keyboard_fd);
    return 0;
}

