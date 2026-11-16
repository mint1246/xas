using Xas.Core;
using Xas.Core.Discovery;

namespace Xas.Tests;

public static class DiscoveryTests
{
    public static Task RunAsync()
    {
        var service = new LanDiscoveryService("device-a", "Laptop");
        if (service.GetPeers().Count != 0)
        {
            throw new InvalidOperationException(
                "A new discovery service must start with no peers.");
        }
        if (new LanDiscoveryOptions().UdpPort != XasProtocol.DefaultPort)
        {
            throw new InvalidOperationException("Discovery must default to the Xas protocol port.");
        }

        var argumentFailures = new Action[]
        {
            () => new LanDiscoveryService("", "Laptop"),
            () => new LanDiscoveryService("device-a", ""),
            () => new LanDiscoveryService(
                "device-a", "Laptop",
                options: new LanDiscoveryOptions { MulticastAddress = "127.0.0.1" })
        };
        foreach (var action in argumentFailures)
        {
            ExpectException(action, typeof(ArgumentException));
        }

        var rangeFailures = new Action[]
        {
            () => new LanDiscoveryService("device-a", "Laptop", 0),
            () => new LanDiscoveryService(
                "device-a", "Laptop",
                options: new LanDiscoveryOptions
                {
                    AnnouncementInterval = TimeSpan.FromSeconds(10),
                    PeerExpiration = TimeSpan.FromSeconds(10)
                })
        };
        foreach (var action in rangeFailures)
        {
            ExpectException(action, typeof(ArgumentOutOfRangeException));
        }
        return Task.CompletedTask;
    }

    private static void ExpectException(Action action, Type exceptionType)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exceptionType.IsInstanceOfType(exception))
        {
            return;
        }

        throw new InvalidOperationException($"Expected {exceptionType.Name}.");
    }
}
