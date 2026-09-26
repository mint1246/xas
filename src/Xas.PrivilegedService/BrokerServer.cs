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
        var stopped = Task.Run(() => stop.WaitOne());
        while (!stop.WaitOne(0))
        {
            using var pipe = CreatePipe();
            var connected = pipe.WaitForConnectionAsync();
            if (await Task.WhenAny(connected, stopped).ConfigureAwait(false) != connected) return;
            await connected.ConfigureAwait(false);
            try { await HandleAsync(pipe).ConfigureAwait(false); }
            catch (Exception ex)
            {
                try { await AdminBrokerWire.WriteHeaderAsync(pipe, new AdminBrokerStatus(false, ex.Message), CancellationToken.None); }
                catch (IOException) { }
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;AU)";
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

    private static async Task HandleAsync(NamedPipeServerStream pipe)
    {
        var accepted = false;
        try
        {
            using var headerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var request = await AdminBrokerWire.ReadHeaderAsync<AdminBrokerRequest>(pipe, headerTimeout.Token).ConfigureAwait(false);
            using var caller = GetPipeClientIdentity(pipe);
            if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var token = GetElevatedUserToken(caller, session);
            await AdminBrokerWire.WriteHeaderAsync(pipe, new AdminBrokerStatus(true), CancellationToken.None).ConfigureAwait(false);
            accepted = true;
            if (request.Mode == ShellMode.Interactive)
            {
                await RunInteractiveAsync(pipe, token, request).ConfigureAwait(false);
                return;
            }
            var child = StartAsUser(request, token);
            using var process = child.Process;
            using var input = new FileStream(child.Input, FileAccess.Write, 32 * 1024, true);
            using var output = new FileStream(child.Output, FileAccess.Read, 32 * 1024, true);
            using var error = new FileStream(child.Error, FileAccess.Read, 32 * 1024, true);
            var stdoutTask = SendOutputAsync(output, pipe, AdminFrameKind.Stdout);
            var stderrTask = SendOutputAsync(error, pipe, AdminFrameKind.Stderr);
            using var inputCts = new CancellationTokenSource();
            var inputTask = ReceiveInputAsync(pipe, input, process, inputCts.Token);
            await process.WaitForExitAsync().ConfigureAwait(false);
            inputCts.Cancel();
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch (IOException) { }
            try { await inputTask.ConfigureAwait(false); } catch (IOException) { } catch (OperationCanceledException) { }
            var exit = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(exit, process.ExitCode);
            await AdminBrokerWire.WriteFrameAsync(pipe, AdminFrameKind.Exit, exit, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (accepted)
            {
                try { await AdminBrokerWire.WriteFrameAsync(pipe, AdminFrameKind.Error, Encoding.UTF8.GetBytes(ex.Message), CancellationToken.None); } catch (IOException) { }
                return;
            }
            await AdminBrokerWire.WriteHeaderAsync(pipe, new AdminBrokerStatus(false, ex.Message), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task RunInteractiveAsync(NamedPipeServerStream pipe,
        Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, AdminBrokerRequest request)
    {
        if (request.Columns is < 1 or > short.MaxValue || request.Rows is < 1 or > short.MaxValue)
            throw new InvalidDataException("Initial ConPTY dimensions are invalid.");
        await using var session = ElevatedConPtySession.Start(token, request.Columns, request.Rows);
        using var inputStop = new CancellationTokenSource();
        var inputTask = ReceiveConPtyInputAsync(pipe, session, inputStop.Token);
        var outputTask = SendConPtyOutputAsync(session.Output, pipe);
        var exitTask = session.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, inputTask).ConfigureAwait(false);
        if (completed == inputTask && !exitTask.IsCompleted)
        {
            session.Terminate();
        }
        var exitCode = await exitTask.ConfigureAwait(false);
        inputStop.Cancel();
        try { await inputTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (inputStop.IsCancellationRequested) { }
        catch (IOException) { }
        await outputTask.ConfigureAwait(false);
        var exit = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(exit, exitCode);
        await AdminBrokerWire.WriteFrameAsync(pipe, AdminFrameKind.Exit, exit, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task SendConPtyOutputAsync(Stream output, Stream pipe)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await output.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return;
            await AdminBrokerWire.WriteFrameAsync(pipe, AdminFrameKind.Stdout, buffer.AsMemory(0, count), CancellationToken.None).ConfigureAwait(false);
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
            var adminSid = CreateAdminSid();
            bool isAdmin;
            try { if (!CheckTokenMembership(sessionToken, adminSid, out isAdmin) || !isAdmin)
                throw new UnauthorizedAccessException("Administrator privileges are required for brokered commands.");
            }
            finally { LocalFree(adminSid); }
            if (!GetTokenInformation(sessionToken, 18, out var elevationType, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (elevationType == 3 && GetLinkedToken(sessionToken, 19, out var linkedToken, IntPtr.Size, out _))
            {
                if (!GetTokenInformation(linkedToken, 20, out var isElevated, sizeof(int), out _) || isElevated == 0)
                { linkedToken.Dispose(); throw new UnauthorizedAccessException("The linked administrator token is not elevated."); }
                return linkedToken;
            }
            if (!GetTokenInformation(sessionToken, 20, out var elevated, sizeof(int), out _) || elevated == 0)
                throw new UnauthorizedAccessException("The active user does not have an elevated administrator token.");
            if (!DuplicateTokenEx(sessionToken, 0xF01FF, IntPtr.Zero, 2, 1, out var duplicate))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return duplicate;
        }
    }

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

    private static async Task SendOutputAsync(Stream source, Stream pipe, AdminFrameKind kind)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return;
            await AdminBrokerWire.WriteFrameAsync(pipe, kind, buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveInputAsync(Stream pipe, Stream input, Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var (kind, data) = await AdminBrokerWire.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (kind == AdminFrameKind.EndStdin) break;
                if (kind != AdminFrameKind.Stdin) throw new InvalidDataException("Unexpected input frame.");
                await input.WriteAsync(data).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { if (!process.HasExited) process.Kill(true); }
        finally { await input.DisposeAsync().ConfigureAwait(false); }
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
