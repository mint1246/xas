using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xas.Cli;
using Xas.Cli.Interactive;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Services;
using Xas.Core.Transport;
using Xas.Daemon;

namespace Xas.Tests;

public static class IntegrationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "xas-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var serverIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(root, "server"));
            using var clientIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(root, "client"));
            var serverTrust = new PeerTrustStore(Path.Combine(root, "server-trust"));
            var clientTrust = new PeerTrustStore(Path.Combine(root, "client-trust"));
            serverTrust.Approve(clientIdentity.DeviceId, clientIdentity.Fingerprint, "client");
            clientTrust.Approve(serverIdentity.DeviceId, serverIdentity.Fingerprint, "server");
            var permissions = new PeerPermissionStore(Path.Combine(root, "server-trust"));
            permissions.SetAllowed(clientIdentity.DeviceId, Capability.Shell, true);

            var port = ReservePort();
            using var stop = new CancellationTokenSource();
            var daemon = new DaemonHost(serverIdentity, serverTrust, permissions, port);
            var serverTask = daemon.RunAsync(stop.Token);
            try
            {
                await Task.Delay(300);
                await using var tls = await MutualTlsTransport.ConnectAsync("127.0.0.1", port, clientIdentity,
                    clientTrust, serverIdentity.DeviceId, TimeSpan.FromSeconds(10));
                await using var frames = new BinaryFrameConnection(tls.Stream, leaveOpen: true);
                await using var peer = new MultiplexedProtocolPeer(frames, (_, _) =>
                    ValueTask.FromException<ProtocolMessage>(new NotSupportedException()));

                var info = await peer.RequestAsync("device.info", []);
                var device = JsonSerializer.Deserialize<DeviceInfo>(info.Payload);
                Assert(device?.DeviceId == serverIdentity.DeviceId, "Info returned the wrong device.");
                if (device is null) throw new Exception("Info response was empty.");
                Assert(device.Capabilities.Any(c => c.Capability == Capability.Shell && c.Version >= 2), "Streaming shell capability was not advertised.");

                var shellRequest = new ShellRequest(ShellMode.Exec, null, "dotnet",
                    [typeof(IntegrationTests).Assembly.Location, "--echo-args", "hello world"]);
                var reply = await peer.RequestAsync("shell.run", ShellWire.EncodeRequest(shellRequest));
                var result = ShellWire.DecodeResult(reply.Payload);
                Assert(result.ExitCode == 0 && Encoding.UTF8.GetString(result.StandardOutput) == "hello world",
                    "The remote argv shell path failed.");

                var config = new LocalConfiguration(Path.Combine(root, "client-config"));
                config.UpsertPeer(new ConfiguredPeer(serverIdentity.DeviceId, "server", "127.0.0.1", port));
                config.SetDefault(serverIdentity.DeviceId);
                using var output = new MemoryStream();
                using var error = new MemoryStream();
                using var cliClient = new RemoteXasClient(config, clientIdentity, clientTrust, output, error);
                var largeRequest = new ShellRequest(ShellMode.Exec, null, "dotnet",
                    [typeof(IntegrationTests).Assembly.Location, "--echo-output", "1500000"]);
                var largeExit = await cliClient.RunShellAsync(largeRequest, null, CancellationToken.None);
                Assert(largeExit == 0 && output.Length == 1_500_000 && error.Length == 0,
                    "The CLI client did not receive the full binary output stream.");

                output.SetLength(0);
                using var pipedInput = new MemoryStream(Encoding.UTF8.GetBytes("piped input\n"));
                using var pipedClient = new RemoteXasClient(config, clientIdentity, clientTrust, output, error, pipedInput);
                var inputRequest = new ShellRequest(ShellMode.Exec, null, "dotnet",
                    [typeof(IntegrationTests).Assembly.Location, "--echo-stdin"]);
                var inputExit = await pipedClient.RunShellAsync(inputRequest, null, CancellationToken.None);
                Assert(inputExit == 0 && Encoding.UTF8.GetString(output.ToArray()) == "piped input\n",
                    "The CLI client did not forward binary standard input.");

                if (OperatingSystem.IsWindows())
                {
                    using var interactiveInput = new MemoryStream(Encoding.UTF8.GetBytes("echo XAS_INTERACTIVE_OK\r\nexit /b 7\r\n"));
                    using var interactiveOutput = new MemoryStream();
                    using var interactiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var interactiveExit = await InteractiveShellClient.RunAsync(
                        config.Resolve(null)!, clientIdentity, clientTrust, interactiveInput, interactiveOutput,
                        80, 24, interactiveTimeout.Token);
                    var interactiveText = Encoding.UTF8.GetString(interactiveOutput.ToArray());
                    Assert(interactiveExit == 7 && interactiveText.Contains("XAS_INTERACTIVE_OK", StringComparison.Ordinal),
                        $"Networked ConPTY shell failed: exit {interactiveExit}, output '{interactiveText}'.");
                }

                permissions.SetAllowed(clientIdentity.DeviceId, Capability.Shell, false);
                try
                {
                    await peer.RequestAsync("shell.open", JsonSerializer.SerializeToUtf8Bytes(
                        new { columns = 80, rows = 24, elevated = false }));
                    throw new Exception("Denied interactive shell request succeeded.");
                }
                catch (RemoteProtocolException) { }
                try
                {
                    await peer.RequestAsync("shell.run", ShellWire.EncodeRequest(shellRequest));
                    throw new Exception("Denied shell request succeeded.");
                }
                catch (RemoteProtocolException) { }
            }
            finally
            {
                stop.Cancel();
                await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
