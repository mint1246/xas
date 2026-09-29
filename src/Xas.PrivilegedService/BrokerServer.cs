using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xas.Core;
using Xas.Core.Privileged;

namespace Xas.PrivilegedService;

[SupportedOSPlatform("windows")]
internal static class BrokerServer
{
    private const string PipeName = WindowsAdminBrokerClient.PipeName;

    public static async Task RunAsync(WaitHandle stop)
    {
        using var stopSource = new CancellationTokenSource();
        var stopRegistration = ThreadPool.RegisterWaitForSingleObject(stop,
            static (state, _) =>
            {
                try { ((CancellationTokenSource)state!).Cancel(); }
                catch (ObjectDisposedException) { }
            }, stopSource, Timeout.Infinite, true);
        var stopped = Task.Delay(Timeout.Infinite, stopSource.Token);
        try
        {
            while (!stop.WaitOne(0))
            {
                using var pipe = CreatePipe();
                var connected = pipe.WaitForConnectionAsync();
                if (await Task.WhenAny(connected, stopped).ConfigureAwait(false) != connected) return;
                await connected.ConfigureAwait(false);
                try { await HandleAsync(pipe, stopSource.Token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    try { await AdminBrokerWire.WriteHeaderAsync(pipe, new AdminBrokerStatus(false, ex.Message), stopSource.Token); }
                    catch (IOException) { }
                    catch (OperationCanceledException) { }
                }
            }
        }
        finally { stopRegistration.Unregister(null); }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        // Local interactive user-session daemons may connect. The pipe also uses
        // PIPE_REJECT_REMOTE_CLIENTS below, and HandleAsync independently verifies
        // the connecting process image before accepting any broker request.
        var sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor, InheritHandle = false };
            var handle = CreateNamedPipe($"\\\\.\\pipe\\{PipeName}", 0x00000003 | 0x40000000,
                0x00000000 | 0x00000008, 1, 64 * 1024, 64 * 1024, 0, ref security);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new NamedPipeServerStream(PipeDirection.InOut, true, true, handle);
        }
        finally { LocalFree(descriptor); }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken serviceToken)
    {
        var accepted = false;
        try
        {
            VerifyXasDaemonClient(pipe);
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var request = await AdminBrokerWire.ReadHeaderAsync<AdminBrokerRequest>(pipe, headerTimeout.Token).ConfigureAwait(false);
            using var caller = GetPipeClientIdentity(pipe);
            if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var token = GetElevatedUserToken(caller, session);
            await AdminBrokerWire.WriteHeaderAsync(pipe, new AdminBrokerStatus(true), serviceToken).ConfigureAwait(false);
            accepted = true;
            if (request.Mode == ShellMode.Interactive)
            {
                await RunInteractiveAsync(pipe, token, request, serviceToken).ConfigureAwait(false);
                return;
            }
            var child = StartAsUser(request, token);
            using var process = child.Process;
            using var input = new FileStream(child.Input, FileAccess.Write, 32 * 1024, true);
            using var output = new FileStream(child.Output, FileAccess.Read, 32 * 1024, true);
            using var error = new FileStream(child.Error, FileAccess.Read, 32 * 1024, true);
            using var inputCts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
            var stdoutTask = SendOutputAsync(output, pipe, AdminFrameKind.Stdout, inputCts.Token);
            var stderrTask = SendOutputAsync(error, pipe, AdminFrameKind.Stderr, inputCts.Token);
            var inputTask = ReceiveInputAsync(pipe, input, inputCts.Token);
            await SuperviseChildAsync(process, inputTask, stdoutTask, stderrTask, inputCts).ConfigureAwait(false);
            inputCts.Cancel();
            await IgnoreCleanupFailure(inputTask).ConfigureAwait(false);
            var exit = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(exit, process.ExitCode);
            await AdminBrokerWire.WriteFrameAsync(pipe, AdminFrameKind.Exit, exit, serviceToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (accepted)
            {
                try { await AdminBrokerWire.WriteFrameAsync(pipe, AdminFrameKind.Error, Encoding.UTF8.GetBytes(ex.Message), serviceToken); }
                catch (IOException) { }
                catch (OperationCanceledException) { }
                return;
            }
            try { await AdminBrokerWire.WriteHeaderAsync(pipe, new AdminBrokerStatus(false, ex.Message), serviceToken).ConfigureAwait(false); }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    private static void VerifyXasDaemonClient(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) || processId == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the administrator broker client process.");

        const uint ProcessQueryLimitedInformation = 0x1000;
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the administrator broker client process.");

        var capacity = 32768u;
        var path = new StringBuilder((int)capacity);
        if (!QueryFullProcessImageName(process, 0, path, ref capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the administrator broker client executable.");

        var actual = Path.GetFullPath(path.ToString());
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Xas.Daemon.exe"));
        if (!StringComparer.OrdinalIgnoreCase.Equals(actual, expected))
            throw new UnauthorizedAccessException("Only the installed Xas user-session daemon may use the administrator broker.");
    }

    private static async Task RunInteractiveAsync(NamedPipeServerStream pipe,
        Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, AdminBrokerRequest request, CancellationToken serviceToken)
    {
        if (request.Columns is < 1 or > short.MaxValue || request.Rows is < 1 or > short.MaxValue)
            throw new InvalidDataException("Initial ConPTY dimensions are invalid.");
        await using var session = ElevatedConPtySession.Start(token, request.Columns, request.Rows);
        using var inputStop = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        var inputTask = ReceiveConPtyInputAsync(pipe, session, inputStop.Token);
        var outputTask = SendConPtyOutputAsync(session.Output, pipe, inputStop.Token);
        var exitTask = session.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, inputTask, outputTask).ConfigureAwait(false);
        if (completed != exitTask && !exitTask.IsCompleted)
        {
            session.Terminate();
        }
        var exitCode = await exitTask.ConfigureAwait(false);
        inputStop.Cancel();
        try { await inputTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (inputStop.IsCancellationRequested) { }
        catch (IOException) { }
        try { await outputTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (inputStop.IsCancellationRequested) { }
        var exit = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(exit, exitCode);
        await AdminBrokerWire.WriteFrameAsync(pipe, AdminFrameKind.Exit, exit, serviceToken).ConfigureAwait(false);
    }

    private static async Task SendConPtyOutputAsync(Stream output, Stream pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return;
            await AdminBrokerWire.WriteFrameAsync(pipe, AdminFrameKind.Stdout, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveConPtyInputAsync(NamedPipeServerStream pipe,
        ElevatedConPtySession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (kind, data) = await AdminBrokerWire.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
            switch (kind)
            {
                case AdminFrameKind.Stdin:
                    if (data.Length == 0) throw new InvalidDataException("Empty ConPTY input frame.");
                    await session.Input.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    await session.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case AdminFrameKind.Resize when data.Length == 4:
                    var columns = BinaryPrimitives.ReadInt16LittleEndian(data);
                    var rows = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(2));
                    if (columns < 1 || rows < 1) throw new InvalidDataException("Invalid ConPTY resize dimensions.");
                    await session.ResizeAsync(columns, rows, cancellationToken).ConfigureAwait(false);
                    break;
                case AdminFrameKind.EndStdin when data.Length == 0:
                    // Keep watching the pipe after input closes so disconnect is observed.
                    break;
                default:
                    throw new InvalidDataException("Unexpected elevated ConPTY input frame.");
            }
        }
    }

    private static WindowsIdentity GetPipeClientIdentity(NamedPipeServerStream pipe)
    {
        if (!ImpersonateNamedPipeClient(pipe.SafePipeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!OpenThreadToken(GetCurrentThread(), 0x0008 | 0x0002, true, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token) return new WindowsIdentity(token.DangerousGetHandle());
        }
        finally { RevertToSelf(); }
    }

    private static SafeAccessTokenHandle GetElevatedUserToken(WindowsIdentity caller, uint session)
    {
        if (!WTSQueryUserToken(session, out var sessionToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (sessionToken)
        {
            using var user = new WindowsIdentity(sessionToken.DangerousGetHandle());
            if (!StringComparer.OrdinalIgnoreCase.Equals(user.User?.Value, caller.User?.Value))
                throw new UnauthorizedAccessException("The broker caller is not the active desktop user.");

            if (!GetTokenInformation(sessionToken, TokenElevationTypeInfoClass, out var elevationType, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            switch (elevationType)
            {
                case TokenElevationTypeLimited:
                    if (!GetLinkedToken(sessionToken, TokenLinkedTokenInfoClass, out var linkedToken, IntPtr.Size, out _))
                        throw new UnauthorizedAccessException("The active user does not have a linked administrator token.",
                            new Win32Exception(Marshal.GetLastWin32Error()));
                    using (linkedToken)
                    {
                        EnsureElevatedAdministrator(linkedToken, requireElevationFlag: true);
                        return DuplicatePrimaryToken(linkedToken);
                    }

                case TokenElevationTypeFull:
                    EnsureElevatedAdministrator(sessionToken, requireElevationFlag: true);
                    return DuplicatePrimaryToken(sessionToken);

                case TokenElevationTypeDefault:
                    // With UAC disabled, an administrator can legitimately have no linked token.
                    // Enabled Administrators membership is sufficient here; standard-user tokens fail.
                    EnsureElevatedAdministrator(sessionToken, requireElevationFlag: false);
                    return DuplicatePrimaryToken(sessionToken);

                default:
                    throw new InvalidDataException($"Unexpected Windows token elevation type: {elevationType}.");
            }
        }
    }

    private static void EnsureElevatedAdministrator(SafeAccessTokenHandle token, bool requireElevationFlag)
    {
        var adminSid = CreateAdminSid();
        try
        {
            // CheckTokenMembership requires an impersonation token when an explicit token handle is supplied.
            // WTSQueryUserToken and TOKEN_LINKED_TOKEN both give us primary tokens, so passing either directly
            // fails with ERROR_BAD_TOKEN_TYPE instead of answering the membership question.
            const uint TokenQuery = 0x0008;
            const int SecurityImpersonation = 2;
            const int TokenImpersonation = 2;
            if (!DuplicateTokenEx(token, TokenQuery, IntPtr.Zero, SecurityImpersonation, TokenImpersonation,
                    out var membershipToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not create an administrator membership token.");
            using (membershipToken)
            {
                if (!CheckTokenMembership(membershipToken, adminSid, out var isAdmin))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not verify administrator token membership.");
                if (!isAdmin)
                    throw new UnauthorizedAccessException("Administrator privileges are required for brokered commands.");
            }
        }
        finally { LocalFree(adminSid); }

        if (!requireElevationFlag) return;
        if (!GetTokenInformation(token, TokenElevationInfoClass, out var elevated, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect token elevation state.");
        if (elevated == 0)
            throw new UnauthorizedAccessException("The selected administrator token is not elevated.");
    }

    private static SafeAccessTokenHandle DuplicatePrimaryToken(SafeAccessTokenHandle token)
    {
        const uint TokenAllAccess = 0xF01FF;
        const int SecurityImpersonation = 2;
        const int TokenPrimary = 1;
        if (!DuplicateTokenEx(token, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var duplicate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return duplicate;
    }

    private const int TokenElevationTypeInfoClass = 18;
    private const int TokenLinkedTokenInfoClass = 19;
    private const int TokenElevationInfoClass = 20;
    private const int TokenElevationTypeDefault = 1;
    private const int TokenElevationTypeFull = 2;
    private const int TokenElevationTypeLimited = 3;

    private static IntPtr CreateAdminSid()
    {
        if (!ConvertStringSidToSid("S-1-5-32-544", out var sid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return sid;
    }

    private static (Process Process, SafeFileHandle Input, SafeFileHandle Output, SafeFileHandle Error) StartAsUser(AdminBrokerRequest request, SafeAccessTokenHandle token)
    {
        if (request.Mode is not (ShellMode.Command or ShellMode.Exec)) throw new InvalidDataException("Only non-interactive command and exec requests are supported.");
        string file;
        var args = new List<string>();
        if (request.Mode == ShellMode.Command)
        {
            if (string.IsNullOrWhiteSpace(request.Command)) throw new InvalidDataException("Command text is required.");
            file = Environment.GetEnvironmentVariable("WINDIR") is { Length: > 0 } windir ? Path.Combine(windir, "System32", "cmd.exe") : @"C:\Windows\System32\cmd.exe";
            args.AddRange(["/d", "/s", "/c", request.Command]);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Executable)) throw new InvalidDataException("Executable path is required.");
            file = request.Executable;
            args.AddRange(request.Arguments ?? []);
        }
        var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!CreatePipe(out var childStdout, out var parentStdout, ref sa, 0) ||
            !CreatePipe(out var childStdin, out var parentStdin, ref sa, 0) ||
            !CreatePipe(out var childStderr, out var parentStderr, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        SetHandleInformation(parentStdout, 1, 0); SetHandleInformation(parentStdin, 1, 0); SetHandleInformation(parentStderr, 1, 0);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100, StdInput = childStdin, StdOutput = childStdout, StdError = childStderr };
        var commandLine = new StringBuilder(Quote(file) + " " + string.Join(" ", args.Select(Quote)));
        var cwd = string.IsNullOrWhiteSpace(request.WorkingDirectory) ? null : request.WorkingDirectory;
        if (!CreateEnvironmentBlock(out var environment, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        ProcessInfo info;
        try
        {
            if (!CreateProcessAsUser(token, file, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x08000000 | 0x00000400,
                environment, cwd, ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { DestroyEnvironmentBlock(environment); }
        childStdin.Dispose(); childStdout.Dispose(); childStderr.Dispose();
        CloseHandle(info.Thread);
        CloseHandle(info.Process);
        return (Process.GetProcessById((int)info.ProcessId), parentStdin, parentStdout, parentStderr);
    }

    internal static async Task SuperviseChildAsync(Process process, Task inputTask, Task stdoutTask,
        Task stderrTask, CancellationTokenSource workCancellation)
    {
        var exitTask = process.WaitForExitAsync(workCancellation.Token);
        var pending = new List<Task> { exitTask, inputTask, stdoutTask, stderrTask };
        try
        {
            while (true)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                if (completed == exitTask)
                {
                    await exitTask.ConfigureAwait(false);
                    await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                    return;
                }
                pending.Remove(completed);
                if (completed.IsFaulted || completed.IsCanceled)
                {
                    await completed.ConfigureAwait(false);
                }
            }
        }
        catch
        {
            workCancellation.Cancel();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
            await IgnoreCleanupFailure(inputTask).ConfigureAwait(false);
            await IgnoreCleanupFailure(stdoutTask).ConfigureAwait(false);
            await IgnoreCleanupFailure(stderrTask).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task IgnoreCleanupFailure(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task SendOutputAsync(Stream source, Stream pipe, AdminFrameKind kind, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            await AdminBrokerWire.WriteFrameAsync(pipe, kind, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task ReceiveInputAsync(Stream pipe, Stream input, CancellationToken cancellationToken)
    {
        var stdinClosed = false;
        try
        {
            while (true)
            {
                var (kind, data) = await AdminBrokerWire.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (kind == AdminFrameKind.EndStdin && data.Length == 0 && !stdinClosed)
                {
                    stdinClosed = true;
                    await input.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                if (stdinClosed) throw new InvalidDataException("Input data followed the end-of-stdin frame.");
                if (kind != AdminFrameKind.Stdin) throw new InvalidDataException("Unexpected input frame.");
                await input.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { if (!stdinClosed) await input.DisposeAsync().ConfigureAwait(false); }
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"') builder.Append('\\', backslashes * 2 + 1);
            else builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public string? Reserved; public string? Desktop; public string? Title; public int X; public int Y; public int XSize; public int YSize; public int XCountChars; public int YCountChars; public int FillAttribute; public int Flags; public short ShowWindow; public short Reserved2; public IntPtr Reserved2Ptr; public SafeFileHandle StdInput; public SafeFileHandle StdOutput; public SafeFileHandle StdError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder imageName, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("advapi32.dll")] private static extern bool RevertToSelf();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")] private static extern bool GetLinkedToken(SafeAccessTokenHandle token, int infoClass, out SafeAccessTokenHandle linked, int length, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CheckTokenMembership(SafeAccessTokenHandle token, IntPtr sid, out bool isMember);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSidToSid(string sid, out IntPtr sidPtr);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInfo processInfo);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
