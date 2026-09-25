using System.Runtime.InteropServices;
using Xas.Core;

namespace Xas.Daemon.Input;

/// <summary>Input injection for a real X11 desktop session using XTest. Wayland and Xwayland sessions are intentionally unsupported.</summary>
public sealed class LinuxX11InputBackend : IAbsoluteInputInjectionBackend, IDisposable
{
    private readonly object _sync = new();
    private IntPtr _display;
    private readonly HashSet<ushort> _heldUsages = [];
    private readonly Dictionary<ushort, byte> _keycodes = [];
    private readonly HashSet<ushort> _heldButtons = [];
    private int _verticalWheel;
    private int _horizontalWheel;
    private bool _disposed;

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                if (_disposed || !IsX11Session()) return false;
                return EnsureDisplay();
            }
        }
    }

    public ValueTask InjectAsync(InputEvent inputEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsX11Session() || !EnsureDisplay()) throw new PlatformNotSupportedException("A reachable X11 desktop session with XTest is required; Wayland sessions are not supported.");
            try
            {
                switch (inputEvent.Kind)
                {
                    case InputEventKind.KeepAlive: break;
                    case InputEventKind.Move:
                        if (inputEvent.X != 0 || inputEvent.Y != 0)
                            Check(XTestFakeRelativeMotionEvent(_display, XDefaultScreen(_display), inputEvent.X, inputEvent.Y, 0), "relative mouse motion");
                        break;
                    case InputEventKind.MoveAbsolute:
                        Check(XTestFakeMotionEvent(_display, XDefaultScreen(_display),
                            inputEvent.X, inputEvent.Y, 0), "absolute mouse motion");
                        break;
                    case InputEventKind.Button:
                        InjectButton(inputEvent);
                        break;
                    case InputEventKind.Scroll:
                        InjectScroll(inputEvent.X, inputEvent.Y);
                        break;
                    case InputEventKind.Key:
                        InjectKey(inputEvent);
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(inputEvent), "Unknown input event kind.");
                }
                XFlush(_display);
            }
            catch
            {
                // The transport will close the lease after an injection error. Forget state and reconnect
                // from a clean slate so a later lease cannot inherit stuck-key bookkeeping.
                _heldUsages.Clear(); _keycodes.Clear(); _heldButtons.Clear();
                _verticalWheel = _horizontalWheel = 0;
                CloseDisplay();
                throw;
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_disposed) return ValueTask.CompletedTask;
            if (_display == IntPtr.Zero || !IsX11Session() || !EnsureDisplay())
            {
                ClearHeld();
                return ValueTask.CompletedTask;
            }
            Exception? failure = null;
            foreach (var keycode in _keycodes.Values.Distinct())
                try { Check(XTestFakeKeyEvent(_display, keycode, False, 0), "key release"); } catch (Exception ex) { failure ??= ex; }
            foreach (var button in _heldButtons)
                try { Check(XTestFakeButtonEvent(_display, MapButton(button), False, 0), "button release"); } catch (Exception ex) { failure ??= ex; }
            try { XFlush(_display); XSync(_display, False); } catch (Exception ex) { failure ??= ex; }
            ClearHeld();
            if (failure is not null) { CloseDisplay(); throw failure; }
        }
        return ValueTask.CompletedTask;
    }

    private void InjectButton(InputEvent e)
    {
        var button = MapButton(e.Code);
        Check(XTestFakeButtonEvent(_display, button, e.Down ? True : False, 0), "mouse button event");
        if (e.Down) _heldButtons.Add(e.Code); else _heldButtons.Remove(e.Code);
    }

    private void InjectScroll(int x, int y)
    {
        checked { _verticalWheel += y; _horizontalWheel += x; }
        if (Math.Abs((long)_verticalWheel) > 120_000 || Math.Abs((long)_horizontalWheel) > 120_000)
            throw new ArgumentOutOfRangeException(nameof(x), "X11 scroll is limited to 100 detents per axis per input batch.");
        while (_verticalWheel >= 120) { Wheel(4); _verticalWheel -= 120; }
        while (_verticalWheel <= -120) { Wheel(5); _verticalWheel += 120; }
        while (_horizontalWheel >= 120) { Wheel(7); _horizontalWheel -= 120; }
        while (_horizontalWheel <= -120) { Wheel(6); _horizontalWheel += 120; }
    }

    private void Wheel(uint button)
    {
        Check(XTestFakeButtonEvent(_display, button, True, 0), "wheel press");
        Check(XTestFakeButtonEvent(_display, button, False, 0), "wheel release");
    }

    private void InjectKey(InputEvent e)
    {
        if (!_keycodes.TryGetValue(e.Code, out var keycode))
        {
            var keysym = HidToKeysym(e.Code);
            if (keysym == 0) throw new NotSupportedException($"HID keyboard usage {e.Code} has no X11 keysym mapping.");
            keycode = XKeysymToKeycode(_display, (UIntPtr)keysym);
            if (keycode == 0) throw new NotSupportedException($"The current X11 keyboard map has no keycode for HID usage {e.Code}.");
        }
        if (e.Repeat)
        {
            Check(XTestFakeKeyEvent(_display, keycode, False, 0), "repeat release");
            Check(XTestFakeKeyEvent(_display, keycode, True, 0), "repeat press");
            _heldUsages.Add(e.Code); _keycodes[e.Code] = keycode;
        }
        else if (e.Down)
        {
            Check(XTestFakeKeyEvent(_display, keycode, True, 0), "key press");
            _heldUsages.Add(e.Code); _keycodes[e.Code] = keycode;
        }
        else
        {
            Check(XTestFakeKeyEvent(_display, keycode, False, 0), "key release");
            _heldUsages.Remove(e.Code); _keycodes.Remove(e.Code);
        }
    }

    private bool EnsureDisplay()
    {
        if (_display != IntPtr.Zero) return true;
        try
        {
            if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libX11.so.6", out var x11)) return false;
            NativeLibrary.Free(x11);
            if (!NativeLibrary.TryLoad("libXtst.so.6", out var xtst)) return false;
            NativeLibrary.Free(xtst);
            var displayName = Environment.GetEnvironmentVariable("DISPLAY");
            if (string.IsNullOrWhiteSpace(displayName)) return false;
            _display = XOpenDisplay(displayName);
            if (_display == IntPtr.Zero) return false;
            if (XTestQueryExtension(_display, out _, out _, out _, out _) == 0)
            {
                CloseDisplay();
                return false;
            }
            return true;
        }
        catch (DllNotFoundException) { CloseDisplay(); return false; }
        catch (EntryPointNotFoundException) { CloseDisplay(); return false; }
    }

    private static bool IsX11Session()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return false;
        return string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "x11", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
    }

    private void Check(int result, string operation)
    {
        if (result == 0) throw new IOException($"XTest rejected {operation}.");
    }

    private void ClearHeld()
    {
        _heldUsages.Clear(); _keycodes.Clear(); _heldButtons.Clear();
        _verticalWheel = _horizontalWheel = 0;
    }

    private void CloseDisplay()
    {
        if (_display == IntPtr.Zero) return;
        try { XCloseDisplay(_display); } finally { _display = IntPtr.Zero; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            try { ReleaseAllCore(); } finally { ClearHeld(); CloseDisplay(); _disposed = true; }
        }
    }

    private void ReleaseAllCore()
    {
        if (_display == IntPtr.Zero) return;
        foreach (var keycode in _keycodes.Values.Distinct()) XTestFakeKeyEvent(_display, keycode, False, 0);
        foreach (var button in _heldButtons) XTestFakeButtonEvent(_display, MapButton(button), False, 0);
        XFlush(_display);
    }

    private static uint MapButton(ushort code) => code switch
    {
        1 => 1, // left
        2 => 3, // right
        3 => 2, // middle
        >= 4 and <= 8 => (uint)(code + 4), // avoid X11 wheel buttons 4-7
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    private static UIntPtr HidToKeysym(ushort usage)
    {
        // X11 keysyms from keysymdef.h. This maps physical HID usages to the conventional US XKB symbols;
        // the active keyboard map resolves each symbol to its server keycode.
        if (usage is >= 4 and <= 29) return (UIntPtr)(usage - 4 + 'a');
        if (usage is >= 30 and <= 38) return (UIntPtr)(usage - 30 + '1');
        if (usage == 39) return (UIntPtr)'0';
        return usage switch
        {
            40 => (UIntPtr)0xff0d, 41 => (UIntPtr)0xff1b, 42 => (UIntPtr)0xff08, 43 => (UIntPtr)0xff09,
            44 => (UIntPtr)' ', 45 => (UIntPtr)'-', 46 => (UIntPtr)'=', 47 => (UIntPtr)'[', 48 => (UIntPtr)']',
            49 => (UIntPtr)'\\', 50 => (UIntPtr)'#', 51 => (UIntPtr)';', 52 => (UIntPtr)'\'', 53 => (UIntPtr)'`',
            54 => (UIntPtr)',', 55 => (UIntPtr)'.', 56 => (UIntPtr)'/', 57 => (UIntPtr)0xffe5,
            >= 58 and <= 69 => (UIntPtr)(0xffbe + usage - 58),
            70 => (UIntPtr)0xff61, 71 => (UIntPtr)0xff14, 72 => (UIntPtr)0xff13, 73 => (UIntPtr)0xff63,
            74 => (UIntPtr)0xff50, 75 => (UIntPtr)0xff55, 76 => (UIntPtr)0xffff, 77 => (UIntPtr)0xff57,
            78 => (UIntPtr)0xff56, 79 => (UIntPtr)0xff53, 80 => (UIntPtr)0xff51, 81 => (UIntPtr)0xff54,
            82 => (UIntPtr)0xff52, 83 => (UIntPtr)0xff7f,
            84 => (UIntPtr)0xffaa, 85 => (UIntPtr)0xffab, 86 => (UIntPtr)0xffad, 87 => (UIntPtr)0xffaf,
            88 => (UIntPtr)0xff8d, 89 => (UIntPtr)0xffb1, 90 => (UIntPtr)0xffb2, 91 => (UIntPtr)0xffb3,
            92 => (UIntPtr)0xffb4, 93 => (UIntPtr)0xffb5, 94 => (UIntPtr)0xffb6, 95 => (UIntPtr)0xffb7,
            96 => (UIntPtr)0xffb8, 97 => (UIntPtr)0xffb0, 98 => (UIntPtr)0xffae, 99 => (UIntPtr)0xffac,
            100 => (UIntPtr)'\\', 101 => (UIntPtr)0xff67, 102 => (UIntPtr)0x1008ff2a, 103 => (UIntPtr)0xffbd,
            104 => (UIntPtr)0xffbf, 105 => (UIntPtr)0xffc0, 106 => (UIntPtr)0xffc1, 107 => (UIntPtr)0xffc2,
            108 => (UIntPtr)0xffc3, 109 => (UIntPtr)0xffc4, 110 => (UIntPtr)0xffc5, 111 => (UIntPtr)0xffc6,
            112 => (UIntPtr)0xffc7, 113 => (UIntPtr)0xffc8, 114 => (UIntPtr)0xffc9, 115 => (UIntPtr)0xffc2,
            116 => (UIntPtr)0xffe2, 117 => (UIntPtr)0xffe9, 118 => (UIntPtr)0xffea, 119 => (UIntPtr)0xffe3,
            120 => (UIntPtr)0xffe4, 121 => (UIntPtr)0xffe7, 122 => (UIntPtr)0xffe8, 123 => (UIntPtr)0xffe5,
            124 => (UIntPtr)0xff13, 125 => (UIntPtr)0xffeb, 126 => (UIntPtr)0xffec,
            127 => (UIntPtr)0xffff, 128 => (UIntPtr)0xff67, 129 => (UIntPtr)0xffe5,
            133 => (UIntPtr)0xffeb, 134 => (UIntPtr)0xffec, 135 => (UIntPtr)0xff69, 136 => (UIntPtr)0xff66,
            137 => (UIntPtr)0xff65, 138 => (UIntPtr)0xff6a, 139 => (UIntPtr)0xff6b, 140 => (UIntPtr)0xff68,
            141 => (UIntPtr)0xff67, 142 => (UIntPtr)0xff6c, 143 => (UIntPtr)0xff6d,
            224 => (UIntPtr)0xffe3, 225 => (UIntPtr)0xffe1, 226 => (UIntPtr)0xffe9, 227 => (UIntPtr)0xffeb,
            228 => (UIntPtr)0xffe4, 229 => (UIntPtr)0xffe2, 230 => (UIntPtr)0xffea, 231 => (UIntPtr)0xffec,
            _ => UIntPtr.Zero
        };
    }

    private const int False = 0, True = 1;
    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr XOpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string displayName);
    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern int XFlush(IntPtr display);
    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern int XSync(IntPtr display, int discard);
    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern int XDefaultScreen(IntPtr display);
    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern byte XKeysymToKeycode(IntPtr display, UIntPtr keysym);
    [DllImport("libXtst.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern int XTestQueryExtension(IntPtr display, out int eventBase, out int errorBase, out int majorVersion, out int minorVersion);
    [DllImport("libXtst.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern int XTestFakeRelativeMotionEvent(IntPtr display, int screenNumber, int x, int y, ulong delay);
    [DllImport("libXtst.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern int XTestFakeMotionEvent(IntPtr display, int screenNumber, int x, int y, ulong delay);
    [DllImport("libXtst.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern int XTestFakeButtonEvent(IntPtr display, uint button, int isPress, ulong delay);
    [DllImport("libXtst.so.6", CallingConvention = CallingConvention.Cdecl)] private static extern int XTestFakeKeyEvent(IntPtr display, byte keycode, int isPress, ulong delay);
}
