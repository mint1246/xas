using System.Diagnostics;
using System.IO.Pipes;
using Xas.Core.Privileged;
using Xas.PrivilegedService;

namespace Xas.Tests;

public static class BrokerServerTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        await EndStdinClosesChildInputWhilePipeStaysOpenAsync();
        await DisconnectAfterEndStdinKillsChildAsync();
        await ServiceCancellationKillsChildAsync();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task EndStdinClosesChildInputWhilePipeStaysOpenAsync()
    {
        using var process = StartCommand("more");
        var name = Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        using var inputStop = new CancellationTokenSource();
        var inputTask = BrokerServer.ReceiveInputAsync(server, process.StandardInput.BaseStream, inputStop.Token);
        await AdminBrokerWire.WriteFrameAsync(client, AdminFrameKind.EndStdin, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (client.IsConnected is false)
            throw new InvalidOperationException("Test pipe disconnected before verifying post-EOF behavior.");
        inputStop.Cancel();
        try { await inputTask; }
        catch (OperationCanceledException) { }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Child exited with {process.ExitCode} after EndStdin.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task DisconnectAfterEndStdinKillsChildAsync()
    {
        using var process = StartCommand("more & ping 127.0.0.1 -n 60 >nul");
        var name = Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        using var workCancellation = new CancellationTokenSource();
        var inputTask = BrokerServer.ReceiveInputAsync(server, process.StandardInput.BaseStream, workCancellation.Token);
        await AdminBrokerWire.WriteFrameAsync(client, AdminFrameKind.EndStdin, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        await Task.Delay(250);
        client.Dispose();
        var stdout = Task.Delay(Timeout.Infinite, workCancellation.Token);
        var stderr = Task.Delay(Timeout.Infinite, workCancellation.Token);
        try
        {
            await BrokerServer.SuperviseChildAsync(process, inputTask, stdout, stderr,
                workCancellation).WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("Broker supervision should fail after client disconnect.");
        }
        catch (IOException) { }
        if (!process.HasExited)
            throw new InvalidOperationException("Broker did not kill child after a disconnect following EndStdin.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task ServiceCancellationKillsChildAsync()
    {
        using var process = StartCommand("ping 127.0.0.1 -n 60 >nul");
        using var workCancellation = new CancellationTokenSource();
        var pending = Task.Delay(Timeout.Infinite, workCancellation.Token);
        var supervise = BrokerServer.SuperviseChildAsync(process, pending, pending, pending, workCancellation);
        workCancellation.Cancel();
        try { await supervise.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        if (!process.HasExited)
            throw new InvalidOperationException("Broker did not kill child when service cancellation arrived.");
    }

    private static Process StartCommand(string command)
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", command }
        }) ?? throw new InvalidOperationException("Could not start isolated broker child process.");
        _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        return process;
    }
}
