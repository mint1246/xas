using Xas.Core;
using Xas.Core.Protocol;
using Xas.Input;

namespace Xas.Daemon.Sessions;

/// <summary>Independent, long-lived transport lanes within one authenticated device relationship.</summary>
public enum PeerLane { Control, Realtime, Interactive, Bulk }

public sealed record PeerSessionSnapshot(string DeviceId, string Name, bool Online, bool RealtimeReady,
    string? Endpoint, DateTimeOffset? LastSeen, TimeSpan? RoundTripTime,
    DeviceInfo? Device, DisplayMetadata? Display);

/// <summary>Stable daemon-owned peer state. Lane connections may change without replacing this object.</summary>
public sealed class PeerSession(string deviceId, string name) : IHotInputPeer, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<PeerLane, MultiplexedProtocolPeer> _lanes = [];
    private readonly Dictionary<PeerLane, bool> _preferredLane = [];
    private DeviceInfo? _device;
    private DisplayMetadata? _display;
    private string? _endpoint;
    private DateTimeOffset? _lastSeen;
    private TimeSpan? _roundTripTime;
    private string _name = name;

    public string DeviceId { get; } = deviceId;
    public DisplayMetadata? Display { get { lock (_gate) return _display; } }
    public bool RealtimeReady { get { lock (_gate) return _lanes.ContainsKey(PeerLane.Realtime); } }
    public bool BulkReady { get { lock (_gate) return _lanes.ContainsKey(PeerLane.Bulk); } }
    public bool Online { get { lock (_gate) return _lanes.ContainsKey(PeerLane.Control); } }
    public event Action? Changed;
    public event Action? Disconnected;
    public event Func<PeerSession, PeerLane, ProtocolMessage, ValueTask>? MessageReceived;

    public PeerSessionSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new(DeviceId, _device?.Name ?? _name, _lanes.ContainsKey(PeerLane.Control),
                _lanes.ContainsKey(PeerLane.Realtime), _endpoint, _lastSeen, _roundTripTime, _device, _display);
        }
    }

    internal void UpdateEndpoint(string? endpoint, string? name = null, DateTimeOffset? lastSeen = null,
        bool clearEndpoint = false)
    {
        var changed = false;
        lock (_gate)
        {
            if (endpoint is not null && _endpoint != endpoint) { _endpoint = endpoint; changed = true; }
            if (clearEndpoint && _endpoint is not null) { _endpoint = null; changed = true; }
            if (!string.IsNullOrWhiteSpace(name) && _name != name) { _name = name; changed = true; }
            if (lastSeen is not null) _lastSeen = lastSeen;
        }
        if (changed) Changed?.Invoke();
    }

    internal void UpdateMetadata(DeviceInfo? device = null, DisplayMetadata? display = null,
        TimeSpan? roundTripTime = null, bool clearDisplay = false)
    {
        var changed = false;
        lock (_gate)
        {
            if (device is not null && !Equals(_device, device)) { _device = device; changed = true; }
            if (clearDisplay && _display is not null) { _display = null; changed = true; }
            if (display is not null && !Equals(_display, display)) { _display = display; changed = true; }
            if (roundTripTime is not null) { _roundTripTime = roundTripTime; changed = true; }
        }
        if (changed) Changed?.Invoke();
    }

    internal async ValueTask<bool> AttachAsync(PeerLane lane, MultiplexedProtocolPeer peer, bool preferred = true)
    {
        MultiplexedProtocolPeer? old;
        bool accepted;
        lock (_gate)
        {
            _lanes.TryGetValue(lane, out old);
            accepted = old is null || !_preferredLane.GetValueOrDefault(lane) || preferred;
            if (!accepted) old = null;
            else
            {
                _lanes[lane] = peer;
                _preferredLane[lane] = preferred;
                _lastSeen = DateTimeOffset.UtcNow;
            }
        }
        if (!accepted)
        {
            await peer.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        Changed?.Invoke();
        if (old is not null && !ReferenceEquals(old, peer)) await old.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    internal void Detach(PeerLane lane, MultiplexedProtocolPeer peer)
    {
        var wasRealtime = false;
        lock (_gate)
        {
            if (!_lanes.TryGetValue(lane, out var current) || !ReferenceEquals(current, peer)) return;
            _lanes.Remove(lane);
            _preferredLane.Remove(lane);
            wasRealtime = lane == PeerLane.Realtime;
        }
        if (wasRealtime) Disconnected?.Invoke();
        Changed?.Invoke();
    }

    private MultiplexedProtocolPeer GetLane(PeerLane lane)
    {
        lock (_gate) return _lanes.TryGetValue(lane, out var peer) ? peer :
            throw new IOException($"{lane} lane to {DeviceId} is offline.");
    }

    public ValueTask<ProtocolMessage> RequestAsync(PeerLane lane, string method, byte[] payload,
        CancellationToken cancellationToken = default) =>
        GetLane(lane).RequestAsync(method, payload, cancellationToken: cancellationToken);

    public ValueTask SendAsync(PeerLane lane, ProtocolMessage message, CancellationToken cancellationToken = default) =>
        GetLane(lane).SendAsync(message, cancellationToken);

    public ValueTask<ProtocolMessage> RequestRealtimeAsync(string method, byte[] payload,
        CancellationToken cancellationToken) => RequestAsync(PeerLane.Realtime, method, payload, cancellationToken);

    public ValueTask SendRealtimeAsync(ProtocolMessage message, CancellationToken cancellationToken) =>
        SendAsync(PeerLane.Realtime, message, cancellationToken);

    internal async ValueTask OnMessageAsync(PeerLane lane, ProtocolMessage message)
    {
        if (MessageReceived is not { } handlers) return;
        foreach (Func<PeerSession, PeerLane, ProtocolMessage, ValueTask> handler in handlers.GetInvocationList())
            await handler(this, lane, message).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        MultiplexedProtocolPeer[] lanes;
        bool wasRealtime;
        lock (_gate) { lanes = _lanes.Values.ToArray(); wasRealtime = _lanes.ContainsKey(PeerLane.Realtime); _lanes.Clear(); _preferredLane.Clear(); }
        foreach (var lane in lanes) await lane.DisposeAsync().ConfigureAwait(false);
        if (wasRealtime) Disconnected?.Invoke();
        Changed?.Invoke();
    }
}
