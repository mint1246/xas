using System.Runtime.InteropServices;
using System.Threading.Channels;
using Xas.Core;

namespace Xas.Input;

/// <summary>Rectangle occupied by a remote virtual monitor in Windows desktop coordinates.</summary>
public readonly record struct WindowsCaptureRegion(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public (int X, int Y) MapToRemote(int x, int y, int remoteWidth, int remoteHeight)
    {
        if (!Contains(x, y) || Right <= Left || Bottom <= Top || remoteWidth <= 0 || remoteHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(x), "Pointer and display dimensions must be valid for this region.");
        var sourceWidth = Right - Left;
        var sourceHeight = Bottom - Top;
        return (sourceWidth == 1 ? 0 : (int)((long)(x - Left) * (remoteWidth - 1) / (sourceWidth - 1)),
            sourceHeight == 1 ? 0 : (int)((long)(y - Top) * (remoteHeight - 1) / (sourceHeight - 1)));
    }
}

/// <summary>Captures physical input on a dedicated Windows message thread.</summary>
public sealed class WindowsInputCapture : IAsyncDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const uint WmQuit = 0x0012, WmInput = 0x00FF;
    private const uint RidInput = 0x10000003, RidevInputSink = 0x00000100;
    private const uint HcAction = 0;
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const uint WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmRButtonDown = 0x0204, WmRButtonUp = 0x0205,
        WmMButtonDown = 0x0207, WmMButtonUp = 0x0208, WmMouseWheel = 0x020A, WmXButtonDown = 0x020B,
        WmXButtonUp = 0x020C, WmMouseHWheel = 0x020E;
    private const uint WmMouseMove = 0x0200;
    private const uint RiMouse = 0, RiKeyboard = 1;
    private const ushort RidevPage = 0x01, RidevUsageMouse = 0x02, RidevUsageKeyboard = 0x06;
    private const uint WmAppStop = 0x8001;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly WindowProc WindowCallback = WindowProcedure;

    private readonly Channel<InputEvent> _events = Channel.CreateBounded<InputEvent>(new BoundedChannelOptions(512)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _window, _keyboardHook, _mouseHook;
    private HookProc? _keyboardProc, _mouseProc;
    private readonly HashSet<ushort> _heldKeys = [];
    private readonly WindowsCaptureRegion? _region;
    private readonly int _remoteWidth, _remoteHeight;
    private Rect _previousClip;
    private bool _cursorClipped;
    private Exception? _failure;
    private int _disposeStarted;
    private int _stopStarted;

    private WindowsInputCapture(WindowsCaptureRegion? region, int remoteWidth, int remoteHeight)
    { _region = region; _remoteWidth = remoteWidth; _remoteHeight = remoteHeight; }
    public static bool IsAvailable => OperatingSystem.IsWindows();
    public ChannelReader<InputEvent> Events => _events.Reader;
    public Task Completion => _finished.Task;

    public static async Task<WindowsInputCapture> StartAsync(CancellationToken cancellationToken = default,
        WindowsCaptureRegion? region = null, int remoteWidth = 0, int remoteHeight = 0)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("Windows input capture is available only on Windows.");
        if (region is not null && (remoteWidth <= 0 || remoteHeight <= 0))
            throw new ArgumentOutOfRangeException(nameof(remoteWidth), "Monitor capture requires remote display dimensions.");
        cancellationToken.ThrowIfCancellationRequested();
        var capture = new WindowsInputCapture(region, remoteWidth, remoteHeight);
        if (Interlocked.CompareExchange(ref _active, capture, null) is not null)
            throw new InvalidOperationException("Input capture is already active in this process.");
        capture._thread = new Thread(capture.Run) { IsBackground = true, Name = "xas input capture" };
        try { capture._thread.Start(); }
        catch { Interlocked.CompareExchange(ref _active, null, capture); throw; }
        using var registration = cancellationToken.Register(static state => ((WindowsInputCapture)state!).Stop(), capture);
        await capture._started.Task.ConfigureAwait(false);
        if (capture._finished.Task.IsCompleted) await capture._finished.Task.ConfigureAwait(false);
        return capture;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        Stop();
        try { await _finished.Task.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) == 0) _stop.Cancel();
        var id = Volatile.Read(ref _threadId);
        if (id != 0) PostThreadMessage(id, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    public void EndCapture() => Stop();

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Create this thread's message queue before Stop can post WM_QUIT.
        try
        {
            if (_stop.IsCancellationRequested) return;
            var instance = GetModuleHandle(null);
            var klass = new WndClass { WindowProc = WindowCallback, ClassName = "XasInputCaptureMessageWindow", Instance = instance };
            RegisterClass(ref klass);
            _window = CreateWindowEx(0, klass.ClassName, string.Empty, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (_region is null)
            {
                var devices = new[]
                {
                    new RawInputDevice { UsagePage = RidevPage, Usage = RidevUsageMouse, Flags = RidevInputSink, Target = _window },
                    new RawInputDevice { UsagePage = RidevPage, Usage = RidevUsageKeyboard, Flags = RidevInputSink, Target = _window }
                };
                if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            _keyboardProc = KeyboardHook;
            _mouseProc = MouseHook;
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, instance, 0);
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, instance, 0);
            if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (_region is null)
            {
                if (!GetClipCursor(out _previousClip) || !GetCursorPos(out var pointer))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var onePixel = new Rect { Left = pointer.X, Top = pointer.Y,
                    Right = checked(pointer.X + 1), Bottom = checked(pointer.Y + 1) };
                if (!ClipCursor(ref onePixel)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                _cursorClipped = true;
            }
            else
            {
                if (!GetCursorPos(out var pointer) || !_region.Value.Contains(pointer.X, pointer.Y))
                    throw new InvalidOperationException("Pointer left the virtual monitor before input capture started.");
            }
            _started.TrySetResult();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Id == WmAppStop) break;
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex) { _failure = ex; _started.TrySetException(ex); }
        finally
        {
            if (_cursorClipped) ClipCursor(ref _previousClip);
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_window != IntPtr.Zero) DestroyWindow(_window);
            Interlocked.CompareExchange(ref _active, null, this);
            if (!_started.Task.IsCompleted) _started.TrySetCanceled();
            _events.Writer.TryComplete(_failure);
            if (_failure is null) _finished.TrySetResult(); else _finished.TrySetException(_failure);
        }
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _stop.IsCancellationRequested) return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        if (_region is { } region && GetCursorPos(out var pointer) && !region.Contains(pointer.X, pointer.Y))
        { Stop(); return CallNextHookEx(_keyboardHook, code, wParam, lParam); }
        var data = Marshal.PtrToStructure<KeyboardData>(lParam);
        if ((data.Flags & 0x10) != 0) return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        var down = wParam.ToInt64() is WmKeyDown or WmSysKeyDown;
        var up = wParam.ToInt64() is WmKeyUp or WmSysKeyUp;
        if (!down && !up) return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        var usage = ScanToHid((ushort)data.ScanCode, (data.Flags & 1) != 0);
        if (usage == 0) { Stop(); return CallNextHookEx(_keyboardHook, code, wParam, lParam); }
        var wasDown = _heldKeys.Contains(usage);
        if (down) _heldKeys.Add(usage); else _heldKeys.Remove(usage);
        if (down && usage == 0x29 && HasCtrl() && HasAlt())
        {
            // Always let the emergency chord reach the local machine, then tear down capture.
            Stop();
            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }
        var repeat = down && wasDown;
        if (!Publish(new InputEvent(InputEventKind.Key, usage, down, repeat))) return new IntPtr(1);
        return new IntPtr(1); // Suppress local keys while capture is active.
    }

    private bool HasCtrl() => _heldKeys.Contains(0xE0) || _heldKeys.Contains(0xE4);
    private bool HasAlt() => _heldKeys.Contains(0xE2) || _heldKeys.Contains(0xE6);

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _stop.IsCancellationRequested) return CallNextHookEx(_mouseHook, code, wParam, lParam);
        var msg = unchecked((uint)wParam.ToInt64());
        var data = Marshal.PtrToStructure<MouseHookData>(lParam);
        if (_region is { } region && !region.Contains(data.Point.X, data.Point.Y))
        { Stop(); return CallNextHookEx(_mouseHook, code, wParam, lParam); }
        if ((data.Flags & 0x0001) != 0) return CallNextHookEx(_mouseHook, code, wParam, lParam);
        var eventHandled = true;
        switch (msg)
        {
            case WmMouseMove when _region is not null:
                var (x, y) = _region.Value.MapToRemote(data.Point.X, data.Point.Y,
                    _remoteWidth, _remoteHeight);
                Publish(new(InputEventKind.MoveAbsolute, X: x, Y: y));
                eventHandled = false; break;
            case WmLButtonDown: case WmLButtonUp: Publish(new(InputEventKind.Button, 1, msg == WmLButtonDown)); break;
            case WmRButtonDown: case WmRButtonUp: Publish(new(InputEventKind.Button, 2, msg == WmRButtonDown)); break;
            case WmMButtonDown: case WmMButtonUp: Publish(new(InputEventKind.Button, 3, msg == WmMButtonDown)); break;
            case WmXButtonDown: case WmXButtonUp:
                var button = unchecked((ushort)((data.MouseData >> 16) & 0xffff)) == 1 ? (ushort)4 : (ushort)5;
                Publish(new(InputEventKind.Button, button, msg == WmXButtonDown)); break;
            case WmMouseWheel:
                Publish(new(InputEventKind.Scroll, X: 0, Y: unchecked((short)(data.MouseData >> 16)))); break;
            case WmMouseHWheel:
                Publish(new(InputEventKind.Scroll, X: unchecked((short)(data.MouseData >> 16)), Y: 0)); break;
            default: eventHandled = false; break;
        }
        // In native monitor mode, Windows moves the logical cursor and we forward its mapped position.
        // Manual capture remains confined and uses Raw Input deltas instead.
        return eventHandled ? new IntPtr(1) : CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private bool Publish(InputEvent item)
    {
        if (_events.Writer.TryWrite(item)) return true;
        Stop(); // Never lose an up event while continuing to suppress local input.
        return false;
    }

    private static ushort ScanToHid(ushort scan, bool extended)
    {
        if (extended) return scan switch
        {
            0x1C => 0x58, 0x1D => 0xE4, 0x35 => 0x54, 0x38 => 0xE6,
            0x47 => 0x4A, 0x48 => 0x52, 0x49 => 0x4B, 0x4B => 0x50,
            0x4D => 0x4F, 0x4F => 0x4D, 0x50 => 0x51, 0x51 => 0x4E,
            0x52 => 0x49, 0x53 => 0x4C, 0x5B => 0xE3, 0x5C => 0xE7, 0x5D => 0x65, _ => 0
        };
        if (scan is >= 0x02 and <= 0x0A) return (ushort)(0x1E + scan - 0x02); // 1..9
        return scan switch
        {
            0x0B => 0x27, // 0
            0x1E => 4, 0x30 => 5, 0x2E => 6, 0x20 => 7, 0x12 => 8,
            0x21 => 9, 0x22 => 10, 0x23 => 11, 0x17 => 12, 0x24 => 13,
            0x25 => 14, 0x26 => 15, 0x32 => 16, 0x31 => 17, 0x18 => 18,
            0x19 => 19, 0x10 => 20, 0x13 => 21, 0x1F => 22, 0x14 => 23,
            0x16 => 24, 0x2F => 25, 0x11 => 26, 0x2D => 27, 0x15 => 28,
            0x2C => 29,
            0x01 => 0x29, 0x0C => 0x2D, 0x0D => 0x2E, 0x0E => 0x2A, 0x0F => 0x2B,
            0x1A => 0x2F, 0x1B => 0x30, 0x1C => 0x28, 0x1D => 0xE0, 0x27 => 0x33,
            0x28 => 0x34, 0x29 => 0x35, 0x2A => 0xE1, 0x2B => 0x31, 0x33 => 0x36,
            0x34 => 0x37, 0x35 => 0x38, 0x36 => 0xE5, 0x37 => 0x55, 0x38 => 0xE2,
            0x39 => 0x2C, 0x3A => 0x39,
            >= 0x3B and <= 0x44 => (ushort)(0x3A + scan - 0x3B),
            0x57 => 0x44, 0x58 => 0x45, 0x45 => 0x53,
            0x52 => 0x62, 0x4F => 0x59, 0x50 => 0x5A, 0x51 => 0x5B, 0x4B => 0x5C,
            0x4C => 0x5D, 0x4D => 0x5E, 0x47 => 0x5F, 0x48 => 0x60, 0x49 => 0x61,
            0x4A => 0x56, 0x4E => 0x57, 0x53 => 0x63, 0x46 => 0x48, _ => 0
        };
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var active = _active;
        if (active is not null && message == WmInput)
        {
            try { active.OnRawInput(lParam); }
            catch (Exception ex) { active._failure = ex; active.Stop(); }
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    private static WindowsInputCapture? _active;

    private void OnRawInput(IntPtr handle)
    {
        if (_region is { } region && GetCursorPos(out var pointer) && !region.Contains(pointer.X, pointer.Y))
        { Stop(); return; }
        uint size = 0;
        GetRawInputData(handle, RidInput, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RawInputHeader>());
        if (size == 0 || size > 4096) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(handle, RidInput, buffer, ref size, (uint)Marshal.SizeOf<RawInputHeader>()) == uint.MaxValue) return;
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RiMouse) return;
            var mouse = Marshal.PtrToStructure<RawMouse>(IntPtr.Add(buffer, Marshal.SizeOf<RawInputHeader>()));
            if ((mouse.Flags & 1) == 0 && (mouse.LastX != 0 || mouse.LastY != 0))
                Publish(new InputEvent(InputEventKind.Move, X: mouse.LastX, Y: mouse.LastY));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private IntPtr KeyboardHook(int code, IntPtr wp, IntPtr lp) => OnKeyboard(code, wp, lp);
    private IntPtr MouseHook(int code, IntPtr wp, IntPtr lp) => OnMouse(code, wp, lp);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Hwnd; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Point; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WndClass { public uint Style; public WindowProc WindowProc; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint VkCode, ScanCode, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public Point Point; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputHeader { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RawMouse { public ushort Flags; private ushort Padding; public uint Buttons; public uint RawButtons; public int LastX, LastY; public uint Extra; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint id, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WndClass windowClass);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClipCursor(ref Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
