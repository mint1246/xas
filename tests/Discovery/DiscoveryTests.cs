using Xas.Core;
using Xas.Core.Discovery;

namespace Xas.Tests;

public static class DiscoveryTests
{
    public static Task RunAsync()
    {
        var service = new LanDiscoveryService("device-a", "Laptop");
        if (service.GetPeers().Count != 0) throw new InvalidOperationException("A new discovery service must start with no peers.");
        if (new LanDiscoveryOptions().UdpPort != XasProtocol.DefaultPort)
            throw new InvalidOperationException("Discovery must default to the Xas protocol port.");

        AssertThrows<ArgumentException>(() => new LanDiscoveryService("", "Laptop"));
        AssertThrows<ArgumentException>(() => new LanDiscoveryService("device-a", ""));
        AssertThrows<ArgumentOutOfRangeException>(() => new LanDiscoveryService("device-a", "Laptop", 0));
        AssertThrows<ArgumentException>(() => new LanDiscoveryService("device-a", "Laptop", options: new LanDiscoveryOptions { MulticastAddress = "127.0.0.1" }));
        AssertThrows<ArgumentOutOfRangeException>(() => new LanDiscoveryService("device-a", "Laptop",
            options: new LanDiscoveryOptions { AnnouncementInterval = TimeSpan.FromSeconds(10), PeerExpiration = TimeSpan.FromSeconds(10) }));
        return Task.CompletedTask;
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
