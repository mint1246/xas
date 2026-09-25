#define _GNU_SOURCE
#include <libei.h>
#include <liboeffis.h>
#include <gio/gio.h>
#include <linux/input-event-codes.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static struct ei *ctx;
static struct oeffis *portal;
static struct ei_device *pointer_device, *absolute_device, *keyboard_device;
static int started_pointer, started_absolute, started_keyboard;
static unsigned char held_keys[256], held_buttons[9];

static uint32_t hid_key(uint16_t u) {
    static const uint16_t letters[] = { KEY_A,KEY_B,KEY_C,KEY_D,KEY_E,KEY_F,KEY_G,KEY_H,KEY_I,KEY_J,KEY_K,KEY_L,KEY_M,KEY_N,KEY_O,KEY_P,KEY_Q,KEY_R,KEY_S,KEY_T,KEY_U,KEY_V,KEY_W,KEY_X,KEY_Y,KEY_Z };
    static const uint16_t digits[] = { KEY_1,KEY_2,KEY_3,KEY_4,KEY_5,KEY_6,KEY_7,KEY_8,KEY_9,KEY_0 };
    if (u >= 4 && u <= 29) return letters[u-4];
    if (u >= 30 && u <= 39) return digits[u-30];
    if (u >= 58 && u <= 67) return KEY_F1 + u - 58;
    if (u == 68) return KEY_F11;
    if (u == 69) return KEY_F12;
    if (u >= 89 && u <= 97) {
        static const uint16_t keypad[] = { KEY_KP1,KEY_KP2,KEY_KP3,KEY_KP4,KEY_KP5,KEY_KP6,KEY_KP7,KEY_KP8,KEY_KP9 };
        return keypad[u-89];
    }
    switch (u) {
    case 40:return KEY_ENTER; case 41:return KEY_ESC; case 42:return KEY_BACKSPACE; case 43:return KEY_TAB; case 44:return KEY_SPACE;
    case 45:return KEY_MINUS; case 46:return KEY_EQUAL; case 47:return KEY_LEFTBRACE; case 48:return KEY_RIGHTBRACE; case 49:return KEY_BACKSLASH;
    case 51:return KEY_SEMICOLON; case 52:return KEY_APOSTROPHE; case 53:return KEY_GRAVE; case 54:return KEY_COMMA; case 55:return KEY_DOT;
    case 56:return KEY_SLASH; case 57:return KEY_CAPSLOCK; case 70:return KEY_SYSRQ; case 71:return KEY_SCROLLLOCK; case 72:return KEY_PAUSE;
    case 73:return KEY_INSERT; case 74:return KEY_HOME; case 75:return KEY_PAGEUP; case 76:return KEY_DELETE; case 77:return KEY_END;
    case 78:return KEY_PAGEDOWN; case 79:return KEY_RIGHT; case 80:return KEY_LEFT; case 81:return KEY_DOWN; case 82:return KEY_UP;
    case 83:return KEY_NUMLOCK; case 84:return KEY_KPSLASH; case 85:return KEY_KPASTERISK; case 86:return KEY_KPMINUS; case 87:return KEY_KPPLUS;
    case 88:return KEY_KPENTER; case 98:return KEY_KP0; case 99:return KEY_KPDOT; case 100:return KEY_102ND; case 101:return KEY_COMPOSE;
    case 224:return KEY_LEFTCTRL; case 225:return KEY_LEFTSHIFT; case 226:return KEY_LEFTALT; case 227:return KEY_LEFTMETA;
    case 228:return KEY_RIGHTCTRL; case 229:return KEY_RIGHTSHIFT; case 230:return KEY_RIGHTALT; case 231:return KEY_RIGHTMETA;
    default:return 0;
    }
}

static void dispatch(void) {
    ei_dispatch(ctx);
    struct ei_event *ev;
    while ((ev = ei_get_event(ctx))) {
        switch (ei_event_get_type(ev)) {
        case EI_EVENT_SEAT_ADDED:
            ei_seat_bind_capabilities(ei_event_get_seat(ev), EI_DEVICE_CAP_POINTER, EI_DEVICE_CAP_POINTER_ABSOLUTE, EI_DEVICE_CAP_BUTTON, EI_DEVICE_CAP_SCROLL, EI_DEVICE_CAP_KEYBOARD, NULL);
            break;
        case EI_EVENT_DEVICE_ADDED: {
            struct ei_device *d = ei_event_get_device(ev);
            if (ei_device_has_capability(d, EI_DEVICE_CAP_POINTER) || ei_device_has_capability(d, EI_DEVICE_CAP_BUTTON) || ei_device_has_capability(d, EI_DEVICE_CAP_SCROLL)) { if (pointer_device) ei_device_unref(pointer_device); pointer_device = ei_device_ref(d); }
            if (ei_device_has_capability(d, EI_DEVICE_CAP_POINTER_ABSOLUTE)) { if (absolute_device) ei_device_unref(absolute_device); absolute_device = ei_device_ref(d); }
            if (ei_device_has_capability(d, EI_DEVICE_CAP_KEYBOARD)) { if (keyboard_device) ei_device_unref(keyboard_device); keyboard_device = ei_device_ref(d); }
            break;
        }
        case EI_EVENT_DEVICE_START_EMULATING: {
            struct ei_device *d = ei_event_get_device(ev);
            ei_device_start_emulating(d, ei_event_emulating_get_sequence(ev));
            if (d == pointer_device) started_pointer = 1;
            if (d == absolute_device) started_absolute = 1;
            if (d == keyboard_device) started_keyboard = 1;
            break;
        }
        case EI_EVENT_DEVICE_REMOVED:
            // A portal can revoke a device at any time. End this lease before another
            // stdin command can dereference a removed device; the parent reconnects.
            fprintf(stderr, "EIS device removed\n");
            exit(2);
        case EI_EVENT_DISCONNECT: fprintf(stderr, "EIS disconnected\n"); exit(2);
        default: break;
        }
        ei_event_unref(ev);
    }
}

static int wait_devices(void) {
    while (!pointer_device || !absolute_device || !keyboard_device || !started_pointer || !started_absolute || !started_keyboard) {
        struct pollfd p = { .fd = ei_get_fd(ctx), .events = POLLIN };
        if (poll(&p, 1, 30000) <= 0) return -1;
        dispatch();
    }
    return 0;
}

static int connect_portal(void) {
    portal = oeffis_new(NULL);
    if (!portal) { fprintf(stderr,"Could not create liboeffis context\n"); return -1; }
    oeffis_create_session(portal, OEFFIS_DEVICE_KEYBOARD | OEFFIS_DEVICE_POINTER);
    struct pollfd p = { .fd = oeffis_get_fd(portal), .events = POLLIN };
    for (;;) {
        if (poll(&p,1,30000) <= 0) { fprintf(stderr,"Timed out waiting for RemoteDesktop portal consent\n"); return -1; }
        oeffis_dispatch(portal);
        enum oeffis_event_type event;
        while ((event = oeffis_get_event(portal)) != OEFFIS_EVENT_NONE) {
            if (event == OEFFIS_EVENT_CONNECTED_TO_EIS) {
                int fd = oeffis_get_eis_fd(portal);
                if (fd < 0) { perror("oeffis_get_eis_fd"); return -1; }
                ctx = ei_new_sender(NULL);
                if (!ctx) { close(fd); fprintf(stderr,"Could not create libei sender context\n"); return -1; }
                if (ei_setup_backend_fd(ctx,fd) < 0) { fprintf(stderr,"libei rejected the portal EIS fd\n"); return -1; }
                return 0;
            }
            if (event == OEFFIS_EVENT_CLOSED || event == OEFFIS_EVENT_DISCONNECTED) {
                const char *reason = oeffis_get_error_message(portal);
                fprintf(stderr,"RemoteDesktop portal setup failed: %s\n", reason ? reason : "session closed"); return -1;
            }
        }
    }
}

static int button_code(unsigned c) {
    switch(c) { case 1:return BTN_LEFT; case 2:return BTN_RIGHT; case 3:return BTN_MIDDLE; case 4:return BTN_SIDE; case 5:return BTN_EXTRA; case 6:return BTN_FORWARD; case 7:return BTN_BACK; case 8:return BTN_TASK; default:return 0; }
}

static int portal_probe(void) {
    GError *error = NULL;
    GDBusConnection *bus = g_bus_get_sync(G_BUS_TYPE_SESSION, NULL, &error);
    if (!bus) { if (error) g_error_free(error); return 0; }
    GVariant *reply = g_dbus_connection_call_sync(bus,
        "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
        "org.freedesktop.DBus.Properties", "GetAll", g_variant_new("(s)", "org.freedesktop.portal.RemoteDesktop"),
        G_VARIANT_TYPE("(a{sv})"), G_DBUS_CALL_FLAGS_NONE, 1500, NULL, &error);
    g_object_unref(bus);
    if (!reply) { if (error) g_error_free(error); return 0; }
    GVariant *properties = g_variant_get_child_value(reply, 0);
    guint32 version = 0, devices = 0;
    gboolean valid = g_variant_lookup(properties, "version", "u", &version)
        && g_variant_lookup(properties, "AvailableDeviceTypes", "u", &devices);
    g_variant_unref(properties); g_variant_unref(reply);
    return valid && version >= 2 && (devices & 3u) == 3u;
}

static int handle(char *line) {
    char op; int x=0,y=0,down=0,repeat=0; unsigned code=0;
    if (sscanf(line, "%c", &op) != 1) return -1;
    if (op == 'R') {
        for (unsigned i=0;i<256;i++) if (held_keys[i]) { uint32_t key=hid_key((uint16_t)i); if(key) ei_device_keyboard_key(keyboard_device,key,false); held_keys[i]=0; }
        for (unsigned i=1;i<=8;i++) if (held_buttons[i]) { int b=button_code(i); if(b) ei_device_button_button(pointer_device,(uint32_t)b,false); held_buttons[i]=0; }
        ei_device_frame(pointer_device,ei_now(ctx)); ei_device_frame(keyboard_device,ei_now(ctx));
        puts("OK"); fflush(stdout);
        return 0;
    }
    if (op == 'N') { puts("OK"); fflush(stdout); return 0; }
    if (op == 'M' && sscanf(line, "M %d %d", &x,&y)==2) ei_device_pointer_motion(pointer_device,x,y);
    else if (op == 'A' && sscanf(line, "A %d %d", &x,&y)==2) { if (!absolute_device || !started_absolute || !ei_device_has_capability(absolute_device, EI_DEVICE_CAP_POINTER_ABSOLUTE)) { puts("ERR portal did not grant an active absolute pointer device"); fflush(stdout); return 0; } if (!ei_device_get_region_at(absolute_device,(double)x,(double)y)) { puts("ERR absolute pointer coordinates are outside the portal-granted device regions"); fflush(stdout); return 0; } ei_device_pointer_motion_absolute(absolute_device,x,y); }
    else if (op == 'B' && sscanf(line, "B %u %d", &code,&down)==2) { int b=button_code(code); if(!b)return -1; ei_device_button_button(pointer_device,(uint32_t)b,down!=0); held_buttons[code]=(unsigned char)(down!=0); }
    else if (op == 'K' && sscanf(line, "K %u %d %d", &code,&down,&repeat)==3) { uint32_t key=hid_key((uint16_t)code); if(!key)return -1; if(repeat) { ei_device_keyboard_key(keyboard_device,key,false); ei_device_keyboard_key(keyboard_device,key,true); } else ei_device_keyboard_key(keyboard_device,key,down!=0); held_keys[code]=(unsigned char)(repeat||down); }
    else if (op == 'S' && sscanf(line, "S %d %d", &x,&y)==2) ei_device_scroll_discrete(pointer_device,x,y);
    else return -1;
    if (pointer_device) ei_device_frame(pointer_device,ei_now(ctx));
    if (absolute_device && absolute_device != pointer_device) ei_device_frame(absolute_device,ei_now(ctx));
    if (keyboard_device) ei_device_frame(keyboard_device,ei_now(ctx));
    puts("OK"); fflush(stdout); return 0;
}

int main(int argc, char **argv) {
    if (argc == 2 && strcmp(argv[1], "--probe") == 0) {
        if (!getenv("WAYLAND_DISPLAY") || !portal_probe()) return 1;
        struct oeffis *probe = oeffis_new(NULL);
        struct ei *ei_probe = ei_new_sender(NULL);
        int available = probe != NULL && ei_probe != NULL;
        if (ei_probe) ei_unref(ei_probe);
        if (probe) oeffis_unref(probe);
        return available ? 0 : 1;
    }
    if (!getenv("WAYLAND_DISPLAY")) return 1;
    if (connect_portal() < 0) return 1;
    struct pollfd p = { .fd = ei_get_fd(ctx), .events = POLLIN };
    if (poll(&p,1,30000) <= 0) return 1;
    dispatch();
    if (wait_devices() < 0) { fprintf(stderr,"Timed out waiting for portal-granted EIS devices\n"); return 1; }
    puts("READY"); fflush(stdout);
    char *line=NULL; size_t cap=0;
    for (;;) {
        struct pollfd fds[] = {
            { .fd = STDIN_FILENO, .events = POLLIN },
            { .fd = ei_get_fd(ctx), .events = POLLIN }
        };
        if (poll(fds, 2, -1) < 0) break;
        if (fds[1].revents) dispatch();
        if (fds[0].revents & (POLLERR | POLLHUP | POLLNVAL)) break;
        if (fds[0].revents & POLLIN) {
            if (getline(&line,&cap,stdin) < 0) break;
            int r=handle(line);
            if (r==1) break;
            if (r<0) { puts("ERR unsupported input event"); fflush(stdout); }
            dispatch();
        }
    }
    free(line);
    ei_unref(ctx); oeffis_unref(portal); return 0;
}
