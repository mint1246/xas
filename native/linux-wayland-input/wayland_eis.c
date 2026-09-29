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
#include "../linux-hid-key.h"
#include "../linux-input-wire.h"

static struct ei *ctx;
static struct oeffis *portal;
static struct ei_device *pointer_device, *absolute_device, *keyboard_device;
static int started_pointer, started_absolute, started_keyboard;

enum { ROLE_POINTER = 1, ROLE_ABSOLUTE = 2, ROLE_KEYBOARD = 4 };
enum { INPUT_KEYBOARD = 1, INPUT_RELATIVE = 2, INPUT_ABSOLUTE = 4, INPUT_SCROLL = 8, INPUT_BUTTONS = 16 };

/* The portal may report one device carrying several roles, so roles are a bitmask. */
static int roles_of(struct ei_device *d) {
    int roles = 0;
    if (ei_device_has_capability(d, EI_DEVICE_CAP_POINTER) || ei_device_has_capability(d, EI_DEVICE_CAP_BUTTON)
        || ei_device_has_capability(d, EI_DEVICE_CAP_SCROLL)) roles |= ROLE_POINTER;
    if (ei_device_has_capability(d, EI_DEVICE_CAP_POINTER_ABSOLUTE)) roles |= ROLE_ABSOLUTE;
    if (ei_device_has_capability(d, EI_DEVICE_CAP_KEYBOARD)) roles |= ROLE_KEYBOARD;
    return roles;
}
static unsigned char held_keys[256], held_buttons[9];

static int granted_capabilities(void) {
    int caps = 0;
    if (keyboard_device && started_keyboard && ei_device_has_capability(keyboard_device, EI_DEVICE_CAP_KEYBOARD)) caps |= INPUT_KEYBOARD;
    if (pointer_device && started_pointer && ei_device_has_capability(pointer_device, EI_DEVICE_CAP_POINTER)) caps |= INPUT_RELATIVE;
    if (absolute_device && started_absolute && ei_device_has_capability(absolute_device, EI_DEVICE_CAP_POINTER_ABSOLUTE)) caps |= INPUT_ABSOLUTE;
    if ((pointer_device && started_pointer && ei_device_has_capability(pointer_device, EI_DEVICE_CAP_SCROLL)) ||
        (absolute_device && started_absolute && ei_device_has_capability(absolute_device, EI_DEVICE_CAP_SCROLL))) caps |= INPUT_SCROLL;
    if ((pointer_device && started_pointer && ei_device_has_capability(pointer_device, EI_DEVICE_CAP_BUTTON)) ||
        (absolute_device && started_absolute && ei_device_has_capability(absolute_device, EI_DEVICE_CAP_BUTTON))) caps |= INPUT_BUTTONS;
    return caps;
}


static void dispatch(void) {
    ei_dispatch(ctx);
    struct ei_event *ev;
    while ((ev = ei_get_event(ctx))) {
        /* XAS_EIS_DEBUG=1 traces the raw event stream, which is the only way to tell a missing
           emulator event apart from a device we failed to match. */
        if (getenv("XAS_EIS_DEBUG")) {
            fprintf(stderr, "eis event %d\n", ei_event_get_type(ev));
            if (ei_event_get_type(ev) == EI_EVENT_DEVICE_ADDED) {
                struct ei_device *d = ei_event_get_device(ev);
                /* The portal decides the coordinate space absolute motion is expressed in, so report it
                   rather than assuming it matches the logical desktop we send. */
                fprintf(stderr, "  device name=%s type=%d size=%dx%d roles=%d\n",
                        ei_device_get_name(d) ? ei_device_get_name(d) : "?",
                        ei_device_get_type(d), ei_device_get_width(d), ei_device_get_height(d),
                        roles_of(d));
                for (int i = 0; i < 16; i++) {
                    struct ei_region *r = ei_device_get_region(d, i);
                    if (!r) break;
                    fprintf(stderr, "  region %d: %dx%d at %d,%d physical_scale=%f\n", i,
                            ei_region_get_width(r), ei_region_get_height(r),
                            ei_region_get_x(r), ei_region_get_y(r),
                            (double)ei_region_get_physical_scale(r));
                }
            }
        }
        switch (ei_event_get_type(ev)) {
        case EI_EVENT_SEAT_ADDED:
            ei_seat_bind_capabilities(ei_event_get_seat(ev), EI_DEVICE_CAP_POINTER, EI_DEVICE_CAP_POINTER_ABSOLUTE, EI_DEVICE_CAP_BUTTON, EI_DEVICE_CAP_SCROLL, EI_DEVICE_CAP_KEYBOARD, NULL);
            break;
        case EI_EVENT_DEVICE_ADDED: {
            struct ei_device *d = ei_event_get_device(ev);
            int roles = roles_of(d);
            if (roles & ROLE_POINTER) { if (pointer_device) ei_device_unref(pointer_device); pointer_device = ei_device_ref(d); }
            if (roles & ROLE_ABSOLUTE) { if (absolute_device) ei_device_unref(absolute_device); absolute_device = ei_device_ref(d); }
            if (roles & ROLE_KEYBOARD) { if (keyboard_device) ei_device_unref(keyboard_device); keyboard_device = ei_device_ref(d); }
            break;
        }
        case EI_EVENT_DEVICE_RESUMED: {
            /* EI_EVENT_DEVICE_START_EMULATING is documented as generated only on a receiver context, so a
               sender must never wait for it. Once resumed, the sender starts emulating itself. */
            struct ei_device *d = ei_event_get_device(ev);
            ei_device_start_emulating(d, 0);
            /* Identify the role from its capabilities rather than by comparing ei_device pointers: the
               library hands out a different pointer for each event, so an address comparison never matches. */
            int roles = roles_of(d);
            if (roles & ROLE_POINTER) started_pointer = 1;
            if (roles & ROLE_ABSOLUTE) started_absolute = 1;
            if (roles & ROLE_KEYBOARD) started_keyboard = 1;
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
    while (granted_capabilities() == 0) {
        struct pollfd p = { .fd = ei_get_fd(ctx), .events = POLLIN };
        if (poll(&p, 1, 30000) <= 0) {
            /* Name what never arrived: a portal that grants only a relative pointer is a platform
               limitation worth reporting plainly rather than a generic timeout. */
            fprintf(stderr, "Timed out waiting for portal-granted EIS devices"
                            " (pointer=%d absolute=%d keyboard=%d started=%d/%d/%d)\n",
                    pointer_device != NULL, absolute_device != NULL, keyboard_device != NULL,
                    started_pointer, started_absolute, started_keyboard);
            return -1;
        }
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

static int resolve_absolute_coordinates(double x, double y, double *out_x, double *out_y);

static int handle_event(const unsigned char *event) {
    unsigned kind = event[0], code = xas_u16(event + 1);
    int flags = event[3], down = flags & 1, repeat = flags & 2;
    int x = xas_i32(event + 4), y = xas_i32(event + 8);
    if (kind == 0) return 0;
    if (kind == 1) { if (!pointer_device || !started_pointer || !ei_device_has_capability(pointer_device, EI_DEVICE_CAP_POINTER)) { fprintf(stderr,"ERR portal did not grant relative pointer input\n"); return 0; } ei_device_pointer_motion(pointer_device,x,y); }
    else if (kind == 5) { double ax, ay; if (!absolute_device || !started_absolute || !ei_device_has_capability(absolute_device, EI_DEVICE_CAP_POINTER_ABSOLUTE)) { fprintf(stderr,"ERR portal did not grant absolute pointer input\n"); return 0; } if (!resolve_absolute_coordinates(x,y,&ax,&ay)) { fprintf(stderr,"ERR absolute pointer coordinates are outside portal-granted regions\n"); return 0; } ei_device_pointer_motion_absolute(absolute_device,ax,ay); }
    else if (kind == 2) { int b=button_code(code); if(!b)return -1; struct ei_device *d = pointer_device && started_pointer && ei_device_has_capability(pointer_device, EI_DEVICE_CAP_BUTTON) ? pointer_device : absolute_device; if(!d || !ei_device_has_capability(d, EI_DEVICE_CAP_BUTTON)) { fprintf(stderr,"ERR portal did not grant button input\n"); return 0; } ei_device_button_button(d,(uint32_t)b,down!=0); held_buttons[code]=(unsigned char)(down!=0); }
    else if (kind == 4) { uint32_t key=xas_hid_key((uint16_t)code); if(!key)return -1; if (!keyboard_device || !started_keyboard || !ei_device_has_capability(keyboard_device, EI_DEVICE_CAP_KEYBOARD)) { fprintf(stderr,"ERR portal did not grant keyboard input\n"); return 0; } if(repeat) { ei_device_keyboard_key(keyboard_device,key,false); ei_device_keyboard_key(keyboard_device,key,true); } else ei_device_keyboard_key(keyboard_device,key,down!=0); held_keys[code]=(unsigned char)(repeat||down); }
    else if (kind == 3) { if (!pointer_device || !started_pointer || !ei_device_has_capability(pointer_device, EI_DEVICE_CAP_SCROLL)) { fprintf(stderr,"ERR portal did not grant scroll input\n"); return 0; } ei_device_scroll_discrete(pointer_device,x,y); }
    else return -1;
    return 0;
}

/* XAS coordinates are display-local; EIS regions use compositor-wide origins. */
static int resolve_absolute_coordinates(double x, double y, double *out_x, double *out_y) {
    if (ei_device_get_region_at(absolute_device, x, y)) { *out_x = x; *out_y = y; return 1; }
    for (int i = 0; i < 64; i++) {
        struct ei_region *r = ei_device_get_region(absolute_device, i);
        if (!r) break;
        if (x < 0 || y < 0 || x >= ei_region_get_width(r) || y >= ei_region_get_height(r)) continue;
        double mx = x + ei_region_get_x(r), my = y + ei_region_get_y(r);
        if (ei_device_get_region_at(absolute_device, mx, my)) { *out_x = mx; *out_y = my; return 1; }
    }
    return 0;
}

static void release_all(void) {
        for (unsigned i=0;i<256;i++) if (held_keys[i]) { uint32_t key=xas_hid_key((uint16_t)i); if(key) ei_device_keyboard_key(keyboard_device,key,false); held_keys[i]=0; }
        for (unsigned i=1;i<=8;i++) if (held_buttons[i]) { int b=button_code(i); if(b) ei_device_button_button(pointer_device,(uint32_t)b,false); held_buttons[i]=0; }
        ei_device_frame(pointer_device,ei_now(ctx)); ei_device_frame(keyboard_device,ei_now(ctx));
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
    puts("READY"); printf("CAPS %d\n", granted_capabilities()); fflush(stdout);
    unsigned char *events = malloc(XAS_INPUT_MAX_EVENTS * XAS_INPUT_EVENT_BYTES);
    if (!events) return 1;
    for (;;) {
        struct pollfd fds[] = {
            { .fd = STDIN_FILENO, .events = POLLIN },
            { .fd = ei_get_fd(ctx), .events = POLLIN }
        };
        if (poll(fds, 2, -1) < 0) break;
        if (fds[1].revents) dispatch();
        if (fds[0].revents & (POLLERR | POLLHUP | POLLNVAL)) break;
        if (fds[0].revents & POLLIN) {
            unsigned count=0;
            int status=xas_read_frame(events,&count);
            if (status<=0) break;
            if (count==0) { release_all(); puts("OK"); fflush(stdout); }
            else {
                for (unsigned i=0;i<count;i++) if(handle_event(events+i*XAS_INPUT_EVENT_BYTES)<0) fprintf(stderr,"unsupported input event in batch\n");
                if (pointer_device) ei_device_frame(pointer_device,ei_now(ctx));
                if (absolute_device && absolute_device != pointer_device) ei_device_frame(absolute_device,ei_now(ctx));
                if (keyboard_device) ei_device_frame(keyboard_device,ei_now(ctx));
            }
            dispatch();
        }
    }
    free(events);
    ei_unref(ctx); oeffis_unref(portal); return 0;
}
