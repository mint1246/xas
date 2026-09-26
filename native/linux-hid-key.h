#ifndef XAS_LINUX_HID_KEY_H
#define XAS_LINUX_HID_KEY_H
#include <stdint.h>
#include <linux/input-event-codes.h>

static uint32_t xas_hid_key(uint16_t u) {
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

#endif
