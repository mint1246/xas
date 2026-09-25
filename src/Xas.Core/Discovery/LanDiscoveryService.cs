using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Xas.Core.Discovery;

/// <summary>
/// Announces this device and tracks LAN announcements. Discovery is deliberately not a trust
/// decision: callers must authenticate a peer and authorize capabilities over the TCP protocol.
/// </summary>
public sealed class LanDiscoveryService : IAsyncDisposable
{
    public const int MaxAnnouncementBytes = 512;
    private const string AnnouncementType = "xas-discovery";
    private readonly string _deviceId;
    private readonly string _name;
    private readonly int _tcpPort;
    private readonly LanDiscoveryOptions _options;
    private readonly IPAddress _group;
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredPeer> _peers = new(StringComparer.Ordinal);
    private UdpClient? _client;
    private CancellationTokenSource? _lifetime;
    private Task? _receiveTask;
    private Task? _announceTask;
    private bool _disposed;

    public LanDiscoveryService(string deviceId, string name, int tcpPort = XasProtocol.DefaultPort,
        LanDiscoveryOptions? options = null)
    {
        if (!IsValidText(deviceId, 128)) throw new ArgumentException("Device ID must be 1-128 printable characters.", nameof(deviceId));
        if (!IsValidText(name, 128)) throw new ArgumentException("Name must be 1-128 printable characters.", nameof(name));
        if (tcpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(tcpPort));
        _deviceId = deviceId;
        _name = name;
        _tcpPort = tcpPort;
        _options = options ?? new LanDiscoveryOptions();
        if (_options.UdpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "UDP port must be between 1 and 65535.");
        if (_options.AnnouncementInterval < TimeSpan.FromMilliseconds(250)) throw new ArgumentOutOfRangeException(nameof(options), "Announcement interval must be at least 250 ms.");
        if (_options.PeerExpiration <= _options.AnnouncementInterval) throw new ArgumentOutOfRangeException(nameof(options), "Peer expiration must exceed the announcement interval.");
        if (!IPAddress.TryParse(_options.MulticastAddress, out var group) || group.AddressFamily != AddressFamily.InterNetwork || !IsMulticast(group))
            throw new ArgumentException("A valid IPv4 multicast address is required.", nameof(options));
        _group = group;
    }

    /// <summary>Raised when a peer is first seen or its advertised name/address/port changes.</summary>
    public event Action<DiscoveredPeer>? PeerUpdated;
    /// <summary>Raised when a peer has not announced within PeerExpiration.</summary>
    public event Action<DiscoveredPeer>? PeerExpired;

    public IReadOnlyList<DiscoveredPeer> GetPeers()
    {
        lock (_gate) return _peers.Values.OrderBy(p => p.DeviceId, StringComparer.Ordinal).ToArray();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lifetime is not null) throw new InvalidOperationException("Discovery is already running.");
            cancellationToken.ThrowIfCancellationRequested();
            var client = CreateClient();
            _client = client;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveTask = ReceiveLoopAsync(client, _lifetime.Token);
            _announceTask = AnnounceLoopAsync(client, _lifetime.Token);
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? receive;
        Task? announce;
        lock (_gate)
        {
            _disposed = true;
            if (_lifetime is null) return;
            _lifetime.Cancel();
            _client?.Dispose();
            receive = _receiveTask;
            announce = _announceTask;
        }
        try { await Task.WhenAll(receive!, announce!).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        lock (_gate)
        {
            _lifetime?.Dispose();
            _lifetime = null;
            _client = null;
            _receiveTask = null;
            _announceTask = null;
        }
    }

    private UdpClient CreateClient()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.ExclusiveAddressUse = false;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, _options.UdpPort));
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        client.JoinMulticastGroup(_group);
        return client;
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            if (result.Buffer.Length is 0 or > MaxAnnouncementBytes) continue;
            if (!TryParse(result.Buffer, result.RemoteEndPoint.Address, out var peer) || peer.DeviceId == _deviceId) continue;
            DiscoveredPeer? updated = null;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (_peers.TryGetValue(peer.DeviceId, out var old) && old.Name == peer.Name && old.Address == peer.Address && old.TcpPort == peer.TcpPort)
                    _peers[peer.DeviceId] = old with { LastSeen = now };
                else
                {
                    updated = peer with { LastSeen = now };
                    _peers[peer.DeviceId] = updated;
                }
            }
            if (updated is not null) PeerUpdated?.Invoke(updated);
        }
    }

    private async Task AnnounceLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        var destination = new IPEndPoint(_group, _options.UdpPort);
        while (!cancellationToken.IsCancellationRequested)
        {
            var bytes = CreateAnnouncement();
            await client.SendAsync(bytes, destination, cancellationToken).ConfigureAwait(false);
            ExpirePeers(DateTimeOffset.UtcNow);
            await Task.Delay(_options.AnnouncementInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ExpirePeers(DateTimeOffset now)
    {
        List<DiscoveredPeer> expired = [];
        lock (_gate)
        {
            foreach (var (id, peer) in _peers.ToArray())
                if (now - peer.LastSeen > _options.PeerExpiration) { _peers.Remove(id); expired.Add(peer); }
        }
        foreach (var peer in expired) PeerExpired?.Invoke(peer);
    }

    private byte[] CreateAnnouncement()
    {
        var buffer = new ArrayBufferWriter<byte>(MaxAnnouncementBytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", AnnouncementType);
            writer.WriteNumber("version", XasProtocol.Version);
            writer.WriteString("deviceId", _deviceId);
            writer.WriteString("name", _name);
            writer.WriteNumber("tcpPort", _tcpPort);
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount > MaxAnnouncementBytes) throw new InvalidOperationException("Announcement exceeds the protocol limit.");
        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryParse(byte[] bytes, IPAddress address, out DiscoveredPeer peer)
    {
        peer = null!;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != AnnouncementType ||
                !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var v) || v != XasProtocol.Version ||
                !root.TryGetProperty("deviceId", out var id) || id.ValueKind != JsonValueKind.String || !IsValidText(id.GetString(), 128) ||
                !root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || !IsValidText(name.GetString(), 128) ||
                !root.TryGetProperty("tcpPort", out var port) || !port.TryGetInt32(out var tcpPort) || tcpPort is < 1 or > 65535) return false;
            peer = new DiscoveredPeer(id.GetString()!, name.GetString()!, address.ToString(), tcpPort, DateTimeOffset.UtcNow);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsValidText(string? value, int maxLength) => value is { Length: > 0 } && value.Length <= maxLength && value.All(c => !char.IsControl(c));
    private static bool IsMulticast(IPAddress address) { var first = address.GetAddressBytes()[0]; return first is >= 224 and <= 239; }
}
