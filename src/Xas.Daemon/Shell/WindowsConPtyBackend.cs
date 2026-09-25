using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Xas.Core;

namespace Xas.Daemon.Shell;

/// <summary>Interactive Windows terminal sessions backed by the Windows 10 1809+ ConPTY API.</summary>
public sealed class WindowsConPtyBackend : IInteractiveShellBackend
{
    public bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public Task<IInteractiveShellSession> StartAsync(bool elevated, short columns, short rows,
        CancellationToken cancellationToken)
    {
        if (elevated) throw new NotSupportedException("Elevated interactive shells are not supported.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ConPTY is available only on Windows 10 version 1809 and later.");
        if (!IsAvailable) throw new PlatformNotSupportedException("ConPTY requires Windows 10 version 1809 or later.");
        if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns), "Terminal dimensions must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IInteractiveShellSession>(WindowsConPtySession.Start(columns, rows, cancellationToken));
    }
}

internal sealed class WindowsConPtySession : IInteractiveShellSession
{
    private readonly SafeFileHandle _process;
    private readonly SafeFileHandle _thread;
    private readonly IntPtr _pseudoConsole;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Task<int> _exitTask;
    private int _disposed;
    private int _pseudoClosed;

    private WindowsConPtySession(SafeFileHandle process, SafeFileHandle thread, IntPtr pseudoConsole,
        SafeFileHandle input, SafeFileHandle output)
    {
        _process = process; _thread = thread; _pseudoConsole = pseudoConsole;
        _input = new FileStream(input, FileAccess.Write, 4096, isAsync: false);
        _output = new FileStream(output, FileAccess.Read, 4096, isAsync: false);
        _exitTask = Task.Run(() =>
        {
            Native.WaitForSingleObject(_process, Native.INFINITE);
            var result = Native.GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1;
            ClosePseudoConsole();
            return result;
        });
    }

    internal static WindowsConPtySession Start(short columns, short rows, CancellationToken cancellationToken)
    {
        SafeFileHandle? childInput = null; SafeFileHandle? parentInput = null;
        SafeFileHandle? parentOutput = null; SafeFileHandle? childOutput = null;
        IntPtr hpc = IntPtr.Zero; SafeFileHandle? process = null; SafeFileHandle? thread = null;
        IntPtr attributeList = IntPtr.Zero;
        try
        {
            if (!Native.CreatePipe(out childInput, out parentInput, IntPtr.Zero, 0)) ThrowLast("CreatePipe(input)");
            if (!Native.CreatePipe(out parentOutput, out childOutput, IntPtr.Zero, 0)) ThrowLast("CreatePipe(output)");
            var size = new Native.COORD { X = columns, Y = rows };
            var hr = Native.CreatePseudoConsole(size, childInput, childOutput, 0, out hpc);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            childInput.Dispose(); childInput = null;
            childOutput.Dispose(); childOutput = null;

            var bytes = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attributeList = Marshal.AllocHGlobal(bytes);
            if (!Native.InitializeProcThreadAttributeList(attributeList, 1, 0, ref bytes)) ThrowLast("InitializeProcThreadAttributeList");
            if (!Native.UpdateProcThreadAttribute(attributeList, 0, (IntPtr)Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) ThrowLast("UpdateProcThreadAttribute");

            // Explicit null standard handles prevent redirected parent handles from being duplicated
            // into the child, allowing ConPTY to supply its own terminal handles.
            var startup = new Native.STARTUPINFOEX { StartupInfo = new Native.STARTUPINFO
                { cb = Marshal.SizeOf<Native.STARTUPINFOEX>(), dwFlags = Native.STARTF_USESTDHANDLES }, lpAttributeList = attributeList };
            var startupPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.STARTUPINFOEX>());
            try
            {
                Marshal.StructureToPtr(startup, startupPtr, false);
                var command = new System.Text.StringBuilder("\"" + (Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe") + "\" /Q");
                if (!Native.CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false,
                    Native.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, Environment.CurrentDirectory, startupPtr,
                    out var info)) ThrowLast("CreateProcess(ConPTY)");
                process = new SafeFileHandle(info.hProcess, ownsHandle: true);
                thread = new SafeFileHandle(info.hThread, ownsHandle: true);
            }
            finally { Marshal.FreeHGlobal(startupPtr); }
            cancellationToken.ThrowIfCancellationRequested();
            var session = new WindowsConPtySession(process, thread, hpc, parentInput, parentOutput);
            process = null; thread = null; hpc = IntPtr.Zero; parentInput = null; parentOutput = null;
            return session;
        }
        catch
        {
            if (process is not null)
            {
                try { Native.TerminateProcess(process, 1); } catch { }
            }
            process?.Dispose(); thread?.Dispose(); childInput?.Dispose(); parentInput?.Dispose(); parentOutput?.Dispose(); childOutput?.Dispose();
            if (hpc != IntPtr.Zero) Native.ClosePseudoConsole(hpc);
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero) { Native.DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
        }
    }

    public Stream Input => _input;
    public Stream Output => _output;
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state => ((WindowsConPtySession)state!).Terminate(), this);
        var code = await _exitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        return code;
    }

    public ValueTask ResizeAsync(short columns, short rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        var hr = Native.ResizePseudoConsole(_pseudoConsole, new Native.COORD { X = columns, Y = rows });
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return ValueTask.CompletedTask;
    }

    private void Terminate()
    {
        try { if (!_exitTask.IsCompleted) Native.TerminateProcess(_process, 1); } catch { }
    }

    private void ClosePseudoConsole()
    {
        if (Interlocked.Exchange(ref _pseudoClosed, 1) == 0) Native.ClosePseudoConsole(_pseudoConsole);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Terminate();
        try { await _exitTask.ConfigureAwait(false); } catch { }
        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        ClosePseudoConsole();
        _thread.Dispose(); _process.Dispose();
    }

    private static void ThrowLast(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
}

internal static class Native
{
    internal const uint INFINITE = 0xffffffff, EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const int STARTF_USESTDHANDLES = 0x00000100;
    internal const nuint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    [StructLayout(LayoutKind.Sequential)] internal struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct STARTUPINFO { public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] internal struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] internal struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")] internal static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);
    [DllImport("kernel32.dll")] internal static extern int ResizePseudoConsole(IntPtr hpc, COORD size);
    [DllImport("kernel32.dll")] internal static extern void ClosePseudoConsole(IntPtr hpc);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool CreateProcess(string? app, System.Text.StringBuilder command, IntPtr procAttrs, IntPtr threadAttrs, bool inherit, uint flags, IntPtr env, string cwd, IntPtr startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern uint WaitForSingleObject(SafeFileHandle handle, uint ms);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
}
