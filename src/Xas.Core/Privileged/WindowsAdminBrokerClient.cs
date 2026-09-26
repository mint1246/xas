using System.Buffers.Binary;
using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Xas.Core.Privileged;

/// <summary>Streams an elevated command through the installed local Windows service.</summary>
public sealed class WindowsAdminBrokerClient : IShellBackend
{
    public const string PipeName = "xas-admin-broker";
    public bool SupportsInteractive => false;

    public async Task<int> RunAsync(ShellRequest request, Stream stdin, Stream stdout, Stream stderr,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows administrator shell requires Windows.");
        if (!request.Elevated || request.Mode == ShellMode.Interactive)
            throw new ArgumentException("The administrator broker accepts elevated command or exec requests.", nameof(request));
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
        VerifyInstalledBrokerServer(pipe.SafePipeHandle);
        var message = new AdminBrokerRequest(request.Mode, request.Command, request.Executable,
            request.Arguments.ToArray(), request.WorkingDirectory);
        await AdminBrokerWire.WriteHeaderAsync(pipe, message, cancellationToken).ConfigureAwait(false);
        var status = await AdminBrokerWire.ReadHeaderAsync<AdminBrokerStatus>(pipe, cancellationToken).ConfigureAwait(false);
        if (!status.Ok) throw new UnauthorizedAccessException(status.Error ?? "Administrator broker rejected the request.");

        using var inputCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputTask = PumpInputAsync(stdin, pipe, inputCts.Token);
        try
        {
            while (true)
            {
                var (kind, data) = await AdminBrokerWire.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
                switch (kind)
                {
                    case AdminFrameKind.Stdout:
                        await stdout.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                        break;
                    case AdminFrameKind.Stderr:
                        await stderr.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                        break;
                    case AdminFrameKind.Exit when data.Length == 4:
                        return BinaryPrimitives.ReadInt32LittleEndian(data);
                    case AdminFrameKind.Error:
                        throw new IOException(Encoding.UTF8.GetString(data));
                    default:
                        throw new InvalidDataException("Unexpected administrator broker frame.");
                }
            }
        }
        finally
        {
            inputCts.Cancel();
            try { await inputTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (inputCts.IsCancellationRequested) { }
            catch (IOException) when (inputCts.IsCancellationRequested) { }
        }
    }

    internal static void VerifyInstalledBrokerServer(Microsoft.Win32.SafeHandles.SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var processId)) throw new IOException("Could not identify the administrator broker service.");
        try
        {
            using var server = Process.GetProcessById((int)processId);
            var actual = Path.GetFullPath(server.MainModule?.FileName ?? throw new IOException("Could not identify the administrator broker executable."));
            var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Xas.PrivilegedService.exe"));
            if (!StringComparer.OrdinalIgnoreCase.Equals(actual, expected))
                throw new UnauthorizedAccessException("The named pipe is not owned by the installed Xas administrator broker.");
        }
        catch (ArgumentException ex) { throw new IOException("The administrator broker service process is unavailable.", ex); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("The administrator broker service process could not be verified.", ex); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    private static async Task PumpInputAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            await AdminBrokerWire.WriteFrameAsync(destination, AdminFrameKind.Stdin, buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        await AdminBrokerWire.WriteFrameAsync(destination, AdminFrameKind.EndStdin, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }
}
