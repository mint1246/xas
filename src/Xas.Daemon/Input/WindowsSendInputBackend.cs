using System.ComponentModel;
using System.Runtime.InteropServices;
using Xas.Core;

namespace Xas.Daemon.Input;

/// <summary>Injects a leased input session into the current interactive Windows desktop.</summary>
public sealed class WindowsSendInputBackend : IInputInjectionBackend
{
    private readonly object _gate = new();
    private readonly Dictionary<ushort, (ushort Scan, bool Extended)> _heldKeys = [];
    private readonly HashSet<ushort> _heldButtons = [];
    private bool _failed;

    public bool IsAvailable => OperatingSystem.IsWindows() && IntPtr.Size == 8 && !_failed &&
        Environment.UserInteractive;

    public ValueTask InjectAsync(InputEvent inputEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!IsAvailable) throw new PlatformNotSupportedException("Windows SendInput requires an interactive 64-bit desktop.");
            switch (inputEvent.Kind)
            {
                case InputEventKind.Move:
                    Send(Mouse(inputEvent.X, inputEvent.Y, 0, 0x0001)); break;
                case InputEventKind.Scroll:
                    if (inputEvent.Y != 0) Send(Mouse(0, 0, unchecked((uint)inputEvent.Y), 0x0800));
                    if (inputEvent.X != 0) Send(Mouse(0, 0, unchecked((uint)inputEvent.X), 0x1000));
                    break;
                case InputEventKind.Button:
                    var (flags, data) = Button(inputEvent.Code, inputEvent.Down);
                    Send(Mouse(0, 0, data, flags));
                    if (inputEvent.Down) _heldButtons.Add(inputEvent.Code);
                    else _heldButtons.Remove(inputEvent.Code);
                    break;
                case InputEventKind.Key:
                    var (scan, extended) = HidToScan(inputEvent.Code);
                    Send(Key(scan, extended, inputEvent.Down));
                    if (inputEvent.Down) _heldKeys[inputEvent.Code] = (scan, extended);
                    else _heldKeys.Remove(inputEvent.Code);
                    break;
                case InputEventKind.KeepAlive: break;
                default: throw new ArgumentOutOfRangeException(nameof(inputEvent));
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Exception? first = null;
            foreach (var (_, key) in _heldKeys)
                try { Send(Key(key.Scan, key.Extended, false)); } catch (Exception ex) { first ??= ex; }
            foreach (var button in _heldButtons)
                try { var (flags, data) = Button(button, false); Send(Mouse(0, 0, data, flags)); }
                catch (Exception ex) { first ??= ex; }
            _heldKeys.Clear(); _heldButtons.Clear();
            if (first is not null) { _failed = true; throw first; }
        }
        return ValueTask.CompletedTask;
    }

    private static NativeInput Mouse(int x, int y, uint data, uint flags) => new()
    { Type = 0, Mouse = new NativeMouse { X = x, Y = y, Data = data, Flags = flags } };
    private static NativeInput Key(ushort scan, bool extended, bool down) => new()
    { Type = 1, Keyboard = new NativeKeyboard { Scan = scan,
        Flags = (uint)(0x0008 | (extended ? 0x0001 : 0) | (down ? 0 : 0x0002)) } };

    private static (uint Flags, uint Data) Button(ushort code, bool down) => code switch
    {
        1 => (down ? 0x0002u : 0x0004u, 0),
        2 => (down ? 0x0008u : 0x0010u, 0),
        3 => (down ? 0x0020u : 0x0040u, 0),
        4 => (down ? 0x0080u : 0x0100u, 1u),
        5 => (down ? 0x0080u : 0x0100u, 2u),
        _ => throw new NotSupportedException($"Windows supports mouse button codes 1–5; got {code}.")
    };

    // Set 1 scan codes; extended distinguishes navigation and right-side modifier keys.
    private static (ushort Scan, bool Extended) HidToScan(ushort usage)
    {
        if (usage is >= 30 and <= 38) return ((ushort)(usage - 30 + 0x02), false);
        if (usage is >= 58 and <= 67) return ((ushort)(usage - 58 + 0x3B), false);
        return usage switch
        {
            4 => (0x1E, false), 5 => (0x30, false), 6 => (0x2E, false), 7 => (0x20, false),
            8 => (0x12, false), 9 => (0x21, false), 10 => (0x22, false), 11 => (0x23, false),
            12 => (0x17, false), 13 => (0x24, false), 14 => (0x25, false), 15 => (0x26, false),
            16 => (0x32, false), 17 => (0x31, false), 18 => (0x18, false), 19 => (0x19, false),
            20 => (0x10, false), 21 => (0x13, false), 22 => (0x1F, false), 23 => (0x14, false),
            24 => (0x16, false), 25 => (0x2F, false), 26 => (0x11, false), 27 => (0x2D, false),
            28 => (0x15, false), 29 => (0x2C, false), 39 => (0x0B, false),
            40 => (0x1C, false), 41 => (0x01, false), 42 => (0x0E, false), 43 => (0x0F, false),
            44 => (0x39, false), 45 => (0x0C, false), 46 => (0x0D, false), 47 => (0x1A, false),
            48 => (0x1B, false), 49 => (0x2B, false), 51 => (0x27, false), 52 => (0x28, false),
            53 => (0x29, false), 54 => (0x33, false), 55 => (0x34, false), 56 => (0x35, false),
            57 => (0x3A, false), 68 => (0x57, false), 69 => (0x58, false),
            70 => (0x37, true), 71 => (0x46, false), 72 => (0x45, false),
            73 => (0x52, true), 74 => (0x47, true), 75 => (0x49, true), 76 => (0x53, true),
            77 => (0x4F, true), 78 => (0x51, true), 79 => (0x4D, true), 80 => (0x4B, true),
            81 => (0x50, true), 82 => (0x48, true), 83 => (0x45, false),
            84 => (0x35, true), 85 => (0x37, false), 86 => (0x4A, false), 87 => (0x4E, false),
            88 => (0x1C, true), 89 => (0x4F, false), 90 => (0x50, false), 91 => (0x51, false),
            92 => (0x4B, false), 93 => (0x4C, false), 94 => (0x4D, false), 95 => (0x47, false),
            96 => (0x48, false), 97 => (0x49, false), 98 => (0x52, false), 99 => (0x53, false),
            100 => (0x56, false), 101 => (0x5D, true),
            224 => (0x1D, false), 225 => (0x2A, false), 226 => (0x38, false),
            227 => (0x5B, true), 228 => (0x1D, true), 229 => (0x36, false),
            230 => (0x38, true), 231 => (0x5C, true),
            _ => throw new NotSupportedException($"No Windows scan code for HID keyboard usage {usage}.")
        };
    }

    private static void Send(NativeInput input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed (desktop isolation or UIPI may block injection).");
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct NativeInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public NativeMouse Mouse;
        [FieldOffset(8)] public NativeKeyboard Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse
    { public int X, Y; public uint Data, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboard
    { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count,
        [In] NativeInput[] inputs, int size);
}
