using System.Net;
using System.Net.Sockets;
using Xas.Core.Security;
using Xas.Core.Transport;

namespace Xas.Tests;

public static class TransportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "xas-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var serverIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(root, "server"));
            using var clientIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(root, "client"));
            var serverTrust = new PeerTrustStore(Path.Combine(root, "server-trust"));
            var clientTrust = new PeerTrustStore(Path.Combine(root, "client-trust"));
            serverTrust.Approve(clientIdentity.DeviceId, clientIdentity.Fingerprint, "client");
            clientTrust.Approve(serverIdentity.DeviceId, serverIdentity.Fingerprint, "server");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var accepted = listener.AcceptTcpClientAsync();
                var clientTask = MutualTlsTransport.ConnectAsync("localhost", port, clientIdentity, clientTrust,
                    serverIdentity.DeviceId, TimeSpan.FromSeconds(10));
                using var socket = await accepted;
                AuthenticatedPeerConnection serverConnection;
                try { serverConnection = await MutualTlsTransport.AcceptAsync(socket, serverIdentity, serverTrust, TimeSpan.FromSeconds(10)); }
                catch (Exception serverError)
                {
                    try { await clientTask; }
                    catch (Exception clientError) { throw new AggregateException(serverError, clientError); }
                    throw;
                }
                await using var server = serverConnection;
                await using var client = await clientTask;
                Assert(server.PeerDeviceId == clientIdentity.DeviceId, "Server did not authenticate the client identity.");
                Assert(client.PeerDeviceId == serverIdentity.DeviceId, "Client did not authenticate the server identity.");
            }
            finally { listener.Stop(); }

            var rejectingListener = new TcpListener(IPAddress.Loopback, 0);
            rejectingListener.Start();
            try
            {
                var port = ((IPEndPoint)rejectingListener.LocalEndpoint).Port;
                var accepted = rejectingListener.AcceptTcpClientAsync();
                var wrongTarget = MutualTlsTransport.ConnectAsync("localhost", port, clientIdentity, clientTrust,
                    "xas-00000000000000000000000000000000", TimeSpan.FromSeconds(10));
                using var socket = await accepted;
                try { await MutualTlsTransport.AcceptAsync(socket, serverIdentity, serverTrust, TimeSpan.FromSeconds(10)); }
                catch { }
                try { await wrongTarget; throw new Exception("A mismatched intended peer ID was accepted."); }
                catch (System.Security.Authentication.AuthenticationException) { }
            }
            finally { rejectingListener.Stop(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
