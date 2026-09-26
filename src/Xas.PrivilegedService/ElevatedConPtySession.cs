using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Xas.PrivilegedService;

/// <summary>ConPTY launched with the active administrator user's primary token.</summary>
internal sealed class ElevatedConPtySession : IAsyncDisposable
{
    private const uint Infinite = 0xFFFFFFFF;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nuint PseudoConsoleAttribute = 0x00020016;
    private readonly SafeFileHandle _process;
    private readonly SafeFileHandle _thread;
    private readonly IntPtr _pseudoConsole;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Task<int> _exitTask;
    private int _pseudoClosed;
    private int _disposed;

    private ElevatedConPtySession(SafeFileHandle process, SafeFileHandle thread, IntPtr pseudoConsole,
        SafeFileHandle input, SafeFileHandle output)
    {
        _process = process; _thread = thread; _pseudoConsole = pseudoConsole;
        _input = new FileStream(input, FileAccess.Write, 4096, isAsync: false);
        _output = new FileStream(output, FileAccess.Read, 4096, isAsync: false);
        _exitTask = Task.Run(() =>
        {
            WaitForSingleObject(_process, Infinite);
            var code = GetExitCodeProcess(_process, out var value) ? unchecked((int)value) : -1;
            ClosePseudoConsole();
            return code;
        });
    }

    public Stream Input => _input;
    public Stream Output => _output;
    public Task<int> WaitForExitAsync() => _exitTask;

    public void Terminate()
    {
        if (!_exitTask.IsCompleted) TerminateProcess(_process, 1);
    }

    public static ElevatedConPtySession Start(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, short columns, short rows)
    {
        SafeFileHandle? childInput = null, parentInput = null, parentOutput = null, childOutput = null;
        SafeFileHandle? process = null, thread = null;
        var pseudoConsole = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var startupPointer = IntPtr.Zero;
        var environment = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out childInput, out parentInput, IntPtr.Zero, 0)) ThrowLast("CreatePipe(input)");
            if (!CreatePipe(out parentOutput, out childOutput, IntPtr.Zero, 0)) ThrowLast("CreatePipe(output)");
            var hr = CreatePseudoConsole(new Coord { X = columns, Y = rows }, childInput, childOutput, 0, out pseudoConsole);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            childInput.Dispose(); childInput = null;
            childOutput.Dispose(); childOutput = null;

            var listBytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listBytes);
            attributeList = Marshal.AllocHGlobal(listBytes);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref listBytes)) ThrowLast("InitializeProcThreadAttributeList");
            if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)PseudoConsoleAttribute,
                    pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) ThrowLast("UpdateProcThreadAttribute");

            if (!CreateEnvironmentBlock(out environment, token, false)) ThrowLast("CreateEnvironmentBlock");
            var env = ReadEnvironment(environment);
            var shell = Path.Combine(env.TryGetValue("WINDIR", out var windows) ? windows : @"C:\Windows", "System32", "cmd.exe");
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100 },
                AttributeList = attributeList
            };
            startupPointer = Marshal.AllocHGlobal(Marshal.SizeOf<StartupInfoEx>());
            Marshal.StructureToPtr(startup, startupPointer, false);
            var commandLine = new StringBuilder("\"" + shell + "\" /Q");
            if (!CreateProcessAsUser(token, shell, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | 0x00000400, environment,
                    env.TryGetValue("USERPROFILE", out var profile) ? profile : null,
                    startupPointer, out var info)) ThrowLast("CreateProcessAsUser(ConPTY)");
            process = new SafeFileHandle(info.Process, true);
            thread = new SafeFileHandle(info.Thread, true);
            var session = new ElevatedConPtySession(process, thread, pseudoConsole, parentInput, parentOutput);
            process = null; thread = null; pseudoConsole = IntPtr.Zero; parentInput = null; parentOutput = null;
            return session;
        }
        catch
        {
            if (process is not null) { try { TerminateProcess(process, 1); } catch { } }
            process?.Dispose(); thread?.Dispose(); childInput?.Dispose(); parentInput?.Dispose(); parentOutput?.Dispose(); childOutput?.Dispose();
            if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole);
            throw;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (startupPointer != IntPtr.Zero) Marshal.FreeHGlobal(startupPointer);
            if (attributeList != IntPtr.Zero) { DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
        }
    }

    public async Task ResizeAsync(short columns, short rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var hr = ResizePseudoConsole(_pseudoConsole, new Coord { X = columns, Y = rows });
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_exitTask.IsCompleted) { try { Terminate(); } catch { } }
        try { await _exitTask.ConfigureAwait(false); } catch { }
        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        ClosePseudoConsole();
        _thread.Dispose(); _process.Dispose();
    }

    private void ClosePseudoConsole()
    {
        if (Interlocked.Exchange(ref _pseudoClosed, 1) == 0) ClosePseudoConsole(_pseudoConsole);
    }

    private static Dictionary<string, string> ReadEnvironment(IntPtr environment)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var cursor = environment; Marshal.ReadInt16(cursor) != 0;)
        {
            var entry = Marshal.PtrToStringUni(cursor) ?? string.Empty;
            var split = entry.IndexOf('=');
            if (split > 0) values[entry[..split]] = entry[(split + 1)..];
            cursor = IntPtr.Add(cursor, (entry.Length + 1) * sizeof(char));
        }
        return values;
    }

    private static void ThrowLast(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation);

    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow, Reserved2; public IntPtr Reserved2Ptr, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, IntPtr startup, out ProcessInfo info);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
}
