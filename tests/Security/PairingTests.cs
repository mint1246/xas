using System.Net;
using System.Net.Sockets;
using Xas.Core.Security;
using Xas.Daemon.Pairing;

namespace Xas.Tests;

public static class PairingTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "xas-pairing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var leftIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(root, "left-id"), "left");
            using var rightIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(root, "right-id"), "right");
            var leftTrust = new PeerTrustStore(Path.Combine(root, "left-trust"));
            var rightTrust = new PeerTrustStore(Path.Combine(root, "right-trust"));
            var leftControl = ReserveControlPortPair();
            var rightControl = ReserveControlPortPair(leftControl);

            await using var left = new PairingService(leftIdentity, leftTrust, leftControl + 1, "left");
            await using var right = new PairingService(rightIdentity, rightTrust, rightControl + 1, "right");
            await left.StartAsync();
            await right.StartAsync();

            var outgoing = await left.BeginAsync("127.0.0.1", rightControl + 1, rightIdentity.DeviceId);
            var incoming = await WaitForIncomingAsync(right);

            Assert(!outgoing.Incoming, "Initiating side marked the request as incoming.");
            Assert(incoming.Incoming, "Receiving side did not mark the request as incoming.");
            Assert(outgoing.DeviceId == rightIdentity.DeviceId && incoming.DeviceId == leftIdentity.DeviceId,
                "Pairing pending entries identify the wrong peer.");
            Assert(outgoing.Code == incoming.Code, "Pairing verification codes differ between devices.");
            Assert(outgoing.ControlPort == rightControl && incoming.ControlPort == leftControl,
                "Pairing did not exchange normal control ports.");
            Assert(IsLoopback(outgoing.Address) && IsLoopback(incoming.Address),
                "Pairing did not retain the authenticated peer address.");

            var leftApproval = left.ApproveAsync(outgoing.PairingId, true);
            var rightApproval = right.ApproveAsync(incoming.PairingId, true);
            var results = await Task.WhenAll(leftApproval, rightApproval).WaitAsync(TimeSpan.FromSeconds(10));
            Assert(results.All(v => v), "Mutual pairing approval did not report success to both devices.");
            Assert(leftTrust.List().Any(p => p.DeviceId == rightIdentity.DeviceId), "Left device did not pin right identity.");
            Assert(rightTrust.List().Any(p => p.DeviceId == leftIdentity.DeviceId), "Right device did not pin left identity.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<PairingPending> WaitForIncomingAsync(PairingService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var pending = service.ListPending().FirstOrDefault(p => p.Incoming);
            if (pending is not null) return pending;
            await Task.Delay(25, timeout.Token);
        }
    }

    private static int ReserveControlPortPair(int avoid = -1)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var first = new TcpListener(IPAddress.Any, 0);
            first.Start();
            var port = ((IPEndPoint)first.LocalEndpoint).Port;
            first.Stop();
            if (port >= 65535 || port == avoid || port + 1 == avoid) continue;
            try
            {
                using var control = new TcpListener(IPAddress.Any, port);
                using var pairing = new TcpListener(IPAddress.Any, port + 1);
                control.Start(); pairing.Start();
                control.Stop(); pairing.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new IOException("Could not reserve consecutive loopback ports for pairing test.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool IsLoopback(string address) =>
        IPAddress.TryParse(address, out var parsed) && IPAddress.IsLoopback(parsed);
}
