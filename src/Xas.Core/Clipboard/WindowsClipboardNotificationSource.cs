using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Xas.Core;

/// <summary>Receives WM_CLIPBOARDUPDATE on a dedicated message thread in the user's session.</summary>
internal static class WindowsClipboardNotificationSource
{
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const string WindowClass = "XasClipboardChangeListener";
    private static readonly Native.WindowProcedure Procedure = WindowProc;
    private static readonly Lazy<ushort> ClassAtom = new(RegisterWindowClass);
    private static readonly ConcurrentDictionary<nint, ChannelWriter<bool>> Writers = new();

    public static async IAsyncEnumerable<ClipboardTextSnapshot> WatchAsync(WindowsTextClipboard backend,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await using var pump = new Pump();
        await pump.Ready.ConfigureAwait(false);
        await foreach (var _ in pump.Changes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return await backend.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ushort RegisterWindowClass()
    {
        var definition = new Native.WindowClass
        {
            Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
            Procedure = Procedure,
            Instance = Native.GetModuleHandle(null),
            Name = WindowClass
        };
        var atom = Native.RegisterClassEx(ref definition);
        if (atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed for clipboard listener.");
        return atom;
    }

    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WmClipboardUpdate && Writers.TryGetValue(window, out var writer))
        {
            writer.TryWrite(true);
            return 0;
        }
        if (message == WmClose) { Native.DestroyWindow(window); return 0; }
        if (message == WmDestroy) { Native.PostQuitMessage(0); return 0; }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private sealed class Pump : IAsyncDisposable
    {
        private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public Pump()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "xas clipboard notifications" };
            _thread.Start();
        }

        public Task Ready => _ready.Task;
        public ChannelReader<bool> Changes => _changes.Reader;

        private void Run()
        {
            nint window = 0;
            try
            {
                _ = ClassAtom.Value;
                window = Native.CreateWindowEx(0, WindowClass, string.Empty, 0, 0, 0, 0, 0,
                    new nint(-3), 0, Native.GetModuleHandle(null), 0);
                if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for clipboard listener.");
                Writers[window] = _changes.Writer;
                if (!Native.AddClipboardFormatListener(window))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "AddClipboardFormatListener failed.");
                _ready.TrySetResult(window);
                while (true)
                {
                    var result = Native.GetMessage(out var message, 0, 0, 0);
                    if (result == 0) break;
                    if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed for clipboard listener.");
                    Native.DispatchMessage(ref message);
                }
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
                _changes.Writer.TryComplete(ex);
            }
            finally
            {
                if (window != 0)
                {
                    Native.RemoveClipboardFormatListener(window);
                    Writers.TryRemove(window, out _);
                    if (Native.IsWindow(window)) Native.DestroyWindow(window);
                }
                _changes.Writer.TryComplete();
                _done.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_ready.Task.IsCompletedSuccessfully)
                Native.PostMessage(_ready.Task.Result, WmClose, 0, 0);
            await _done.Task.ConfigureAwait(false);
        }
    }

    private static class Native
    {
        internal delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            internal uint Size;
            internal uint Style;
            internal WindowProcedure Procedure;
            internal int ClassExtra;
            internal int WindowExtra;
            internal nint Instance;
            internal nint Icon;
            internal nint Cursor;
            internal nint Background;
            [MarshalAs(UnmanagedType.LPWStr)] internal string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Name;
            internal nint SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            internal nint Window;
            internal uint Id;
            internal nint WParam;
            internal nint LParam;
            internal uint Time;
            internal int X;
            internal int Y;
            internal uint Private;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WindowClass definition);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowEx(uint extendedStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AddClipboardFormatListener(nint window);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RemoveClipboardFormatListener(nint window);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
        internal static extern int GetMessage(out Message message, nint window, uint minFilter, uint maxFilter);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")]
        internal static extern void PostQuitMessage(int exitCode);
    }
}
