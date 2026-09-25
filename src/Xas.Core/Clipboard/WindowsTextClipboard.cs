using System.ComponentModel;
using System.Runtime.InteropServices;
using Xas.Core;

namespace Xas.Core;

/// <summary>
/// Reads and writes plain text in the current Windows interactive user session. The native
/// CF_UNICODETEXT format is used; .NET strings are Unicode and can be encoded as UTF-8 by callers.
/// Calls run on a worker thread because the Win32 clipboard is synchronous and may be busy.
/// </summary>
public sealed class WindowsTextClipboard : ITextClipboardBackend
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    /// <summary>Whether this host supports the Windows clipboard APIs.</summary>
    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>Gets Unicode clipboard text and the observed system change sequence number.</summary>
    /// <exception cref="PlatformNotSupportedException">The host is not Windows.</exception>
    /// <exception cref="Win32Exception">Windows could not open or read the clipboard.</exception>
    public ValueTask<ClipboardTextSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<ClipboardTextSnapshot>(Task.Run(ReadSnapshot, cancellationToken));
    }

    /// <summary>Replaces clipboard contents with the supplied Unicode text.</summary>
    /// <exception cref="PlatformNotSupportedException">The host is not Windows.</exception>
    /// <exception cref="Win32Exception">Windows could not open or update the clipboard.</exception>
    public ValueTask SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask(Task.Run(() => WriteText(text), cancellationToken));
    }

    private static ClipboardTextSnapshot ReadSnapshot()
    {
        var owner = CreateOwnerWindow();
        if (!Native.OpenClipboard(owner))
        {
            var error = Marshal.GetLastWin32Error();
            Native.DestroyWindow(owner);
            ThrowWin32("OpenClipboard", error);
        }
        try
        {
            string? text = null;
            var handle = Native.GetClipboardData(CfUnicodeText);
            if (handle != IntPtr.Zero)
            {
                var pointer = Native.GlobalLock(handle);
                if (pointer == IntPtr.Zero) ThrowLast("GlobalLock(CF_UNICODETEXT)");
                try { text = Marshal.PtrToStringUni(pointer); }
                finally { Native.GlobalUnlock(handle); }
            }

            // Read while the clipboard remains open, so a writer cannot change the value between
            // reading its contents and sampling the corresponding sequence number.
            var sequence = Native.GetClipboardSequenceNumber();
            return new ClipboardTextSnapshot(text ?? string.Empty, sequence);
        }
        finally
        {
            Native.CloseClipboard();
            Native.DestroyWindow(owner);
        }
    }

    private static void WriteText(string text)
    {
        var bytes = checked((text.Length + 1) * sizeof(char));
        var memory = Native.GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
        if (memory == IntPtr.Zero) ThrowLast("GlobalAlloc");

        var clipboardOwnsMemory = false;
        try
        {
            var pointer = Native.GlobalLock(memory);
            if (pointer == IntPtr.Zero) ThrowLast("GlobalLock");
            try
            {
                Marshal.Copy((text + '\0').ToCharArray(), 0, pointer, text.Length + 1);
            }
            finally { Native.GlobalUnlock(memory); }

            var owner = CreateOwnerWindow();
            if (!Native.OpenClipboard(owner))
            {
                var error = Marshal.GetLastWin32Error();
                Native.DestroyWindow(owner);
                ThrowWin32("OpenClipboard", error);
            }
            try
            {
                if (!Native.EmptyClipboard()) ThrowLast("EmptyClipboard");
                if (Native.SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero) ThrowLast("SetClipboardData");
                clipboardOwnsMemory = true;
            }
            finally
            {
                Native.CloseClipboard();
                Native.DestroyWindow(owner);
            }
        }
        finally
        {
            if (!clipboardOwnsMemory) Native.GlobalFree(memory);
        }
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("The system clipboard backend is available only on Windows.");
    }

    private static void ThrowLast(string operation) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"Clipboard operation {operation} failed.");

    private static void ThrowWin32(string operation, int error) =>
        throw new Win32Exception(error, $"Clipboard operation {operation} failed.");

    private static IntPtr CreateOwnerWindow()
    {
        // A real owner HWND is required: EmptyClipboard assigns ownership to it, and Windows
        // rejects SetClipboardData when OpenClipboard was called with a null owner.
        var owner = Native.CreateWindowEx(0, "STATIC", string.Empty, 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero) ThrowLast("CreateWindowEx(STATIC)");
        return owner;
    }

    private static class Native
    {
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(IntPtr memory);
    }
}
