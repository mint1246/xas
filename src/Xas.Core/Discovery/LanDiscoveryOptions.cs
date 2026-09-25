namespace Xas.Core.Discovery;

/// <summary>Settings for the small, unauthenticated UDP LAN discovery protocol.</summary>
public sealed record LanDiscoveryOptions
{
    /// <summary>Multicast group shared by Xas instances on the local link.</summary>
    public string MulticastAddress { get; init; } = "239.255.47.82";
    /// <summary>UDP port used for discovery. Defaults to the Xas TCP service port.</summary>
    public int UdpPort { get; init; } = XasProtocol.DefaultPort;
    public TimeSpan AnnouncementInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PeerExpiration { get; init; } = TimeSpan.FromSeconds(20);
}
