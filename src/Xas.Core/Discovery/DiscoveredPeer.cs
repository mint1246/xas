namespace Xas.Core.Discovery;

/// <summary>A LAN announcement. It identifies a possible peer; it does not authenticate it.</summary>
public sealed record DiscoveredPeer(string DeviceId, string Name, string Address, int TcpPort,
    DateTimeOffset LastSeen);
