using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xas.Core;
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
                Assert(device.Capabilities.Any(c => c.Capability == Capability.Shell), "Shell capability was not advertised.");

                var shellRequest = new ShellRequest(ShellMode.Exec, null, "dotnet",
                    [typeof(IntegrationTests).Assembly.Location, "--echo-args", "hello world"]);
                var reply = await peer.RequestAsync("shell.run", ShellWire.EncodeRequest(shellRequest));
                var result = ShellWire.DecodeResult(reply.Payload);
                Assert(result.ExitCode == 0 && Encoding.UTF8.GetString(result.StandardOutput) == "hello world",
                    "The remote argv shell path failed.");

                permissions.SetAllowed(clientIdentity.DeviceId, Capability.Shell, false);
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
