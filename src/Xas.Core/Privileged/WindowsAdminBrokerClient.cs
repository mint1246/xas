using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Xas.Core.Privileged;

/// <summary>Streams an elevated command through the installed local Windows service.</summary>
public sealed class WindowsAdminBrokerClient : IShellBackend
{
    public const string PipeName = "xas-admin-broker";
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 0x00000004;
    public bool SupportsInteractive => false;

    public async Task<int> RunAsync(
        ShellRequest request,
        Stream stdin,
        Stream stdout,
        Stream stderr,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Windows administrator shell requires Windows.");
        if (!request.Elevated || request.Mode == ShellMode.Interactive)
        {
            throw new ArgumentException(
                "The administrator broker accepts elevated command or exec requests.",
                nameof(request));
        }

        await using var pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
        VerifyInstalledBrokerServer(pipe.SafePipeHandle);
        var message = new AdminBrokerRequest(
            request.Mode,
            request.Command,
            request.Executable,
            request.Arguments.ToArray(),
            request.WorkingDirectory);
        await AdminBrokerWire.WriteHeaderAsync(pipe, message, cancellationToken).ConfigureAwait(false);
        var status = await AdminBrokerWire.ReadHeaderAsync<AdminBrokerStatus>(
            pipe,
            cancellationToken).ConfigureAwait(false);
        if (!status.Ok)
        {
            throw new UnauthorizedAccessException(
                status.Error ?? "Administrator broker rejected the request.");
        }

        using var inputCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputTask = PumpInputAsync(stdin, pipe, inputCancellation.Token);
        try
        {
            int? exitCode = null;
            while (exitCode is null)
            {
                exitCode = await ReadAndDispatchFrameAsync(
                    pipe,
                    stdout,
                    stderr,
                    cancellationToken).ConfigureAwait(false);
            }

            return exitCode.Value;
        }
        finally
        {
            inputCancellation.Cancel();
            try { await inputTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (inputCancellation.IsCancellationRequested) { }
            catch (IOException) when (inputCancellation.IsCancellationRequested) { }
        }
    }

    private static async Task<int?> DispatchFrameAsync(
        AdminFrameKind kind,
        byte[] data,
        Stream stdout,
        Stream stderr,
        CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case AdminFrameKind.Stdout:
                await stdout.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                return null;
            case AdminFrameKind.Stderr:
                await stderr.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                return null;
            case AdminFrameKind.Exit when data.Length == sizeof(int):
                return BinaryPrimitives.ReadInt32LittleEndian(data);
            case AdminFrameKind.Error:
                throw new IOException(Encoding.UTF8.GetString(data));
            default:
                throw new InvalidDataException("Unexpected administrator broker frame.");
        }
    }

    private static async Task<int?> ReadAndDispatchFrameAsync(
        NamedPipeClientStream pipe,
        Stream stdout,
        Stream stderr,
        CancellationToken cancellationToken)
    {
        var (kind, data) = await AdminBrokerWire.ReadFrameAsync(
            pipe,
            cancellationToken).ConfigureAwait(false);
        return await DispatchFrameAsync(
            kind,
            data,
            stdout,
            stderr,
            cancellationToken).ConfigureAwait(false);
    }

    internal static void VerifyInstalledBrokerServer(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pipeProcessId) || pipeProcessId == 0)
            throw new IOException("Could not identify the administrator broker pipe owner.");
        VerifyBrokerServiceProcessId(pipeProcessId);
    }

    internal static void VerifyBrokerServiceProcessId(uint pipeProcessId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        if (pipeProcessId == 0)
            throw new ArgumentOutOfRangeException(nameof(pipeProcessId));

        const uint ScManagerConnect = 0x0001;
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            throw new IOException(
                "Could not open the Windows Service Control Manager.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            VerifyServiceProcessId(scm, pipeProcessId);
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static void VerifyServiceProcessId(
        IntPtr serviceManager,
        uint pipeProcessId)
    {
        var service = OpenService(
            serviceManager,
            "XasAdminBroker",
            ServiceQueryStatus);
        if (service == IntPtr.Zero)
        {
            throw new IOException(
                "The Xas administrator broker service is not installed or cannot be queried.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            VerifyServiceProcess(service, pipeProcessId);
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    private static void VerifyServiceProcess(
        IntPtr service,
        uint pipeProcessId)
    {
        var bufferSize = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                buffer,
                bufferSize,
                out _))
            {
                throw new IOException(
                    "Could not query the Xas administrator broker service status.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            var status = Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
            if (status.CurrentState != ServiceRunning || status.ProcessId == 0)
                throw new IOException("The Xas administrator broker service is not running.");
            if (status.ProcessId != pipeProcessId)
            {
                throw new UnauthorizedAccessException(
                    "The administrator broker pipe is not owned by the registered Xas service process.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(
        IntPtr serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        IntPtr buffer,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    private static async Task PumpInputAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            await AdminBrokerWire.WriteFrameAsync(
                destination,
                AdminFrameKind.Stdin,
                buffer.AsMemory(0, count),
                ct).ConfigureAwait(false);
        }
        await AdminBrokerWire.WriteFrameAsync(
            destination,
            AdminFrameKind.EndStdin,
            ReadOnlyMemory<byte>.Empty,
            ct).ConfigureAwait(false);
    }
}
