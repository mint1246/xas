using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Discovery;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Services;
using Xas.Core.Transport;
using Xas.Daemon.Clipboard;
using Xas.Daemon.FileTransfer;
using Xas.Daemon.Input;
using Xas.Daemon.Interactive;
using Xas.Daemon.Shell;
using Xas.Daemon.Display;

namespace Xas.Daemon.Sessions;

/// <summary>Owns stable peer state and replaces individual transport lanes as they reconnect.</summary>
public sealed class PeerSessionManager : IAsyncDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly PeerTrustStore _trust;
    private readonly PeerPermissionStore _permissions;
    private readonly InputControlService _input;
    private readonly RequestDispatcher _dispatcher;
    private readonly int _port;
    private readonly object _configGate = new();
    private IReadOnlyList<ConfiguredPeer> _configuredPeers;
    private readonly ConcurrentDictionary<string, PeerSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _dialers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<MultiplexedProtocolPeer, PeerLane> _lanes = new();
    private readonly CancellationTokenSource _shutdown = new();
    private LanDiscoveryService? _discovery;
    private Task? _maintenance;
    private Task? _displayPublisher;
    private int _disposed;

    public PeerSessionManager(DeviceIdentity identity, PeerTrustStore trust, PeerPermissionStore permissions,
        InputControlService input, RequestDispatcher dispatcher, int port = XasProtocol.DefaultPort)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _configuredPeers = new LocalConfiguration().Peers;
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
    }

    public event Action<PeerSessionSnapshot>? SessionChanged;
    public event Action<string>? SessionRemoved;

    public IReadOnlyList<PeerSessionSnapshot> GetSnapshots() => _sessions.Values
        .Select(s => s.Snapshot).OrderBy(s => s.DeviceId, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<DiscoveredPeer> GetDiscoveredPeers() => _discovery?.GetPeers() ?? [];

    public PeerSession? GetSession(string deviceId) => _sessions.GetValueOrDefault(deviceId);

    public void UpdateConfiguredPeers(IReadOnlyList<ConfiguredPeer> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        lock (_configGate) _configuredPeers = peers.ToArray();
        foreach (var peer in peers)
            EnsureSession(peer.DeviceId, peer.Name).UpdateEndpoint($"{peer.Host}:{peer.Port}", peer.Name);
    }

    private IReadOnlyList<ConfiguredPeer> ConfiguredPeers
    { get { lock (_configGate) return _configuredPeers; } }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var discovery = new LanDiscoveryService(_identity.DeviceId, Environment.MachineName, _port);
        discovery.PeerUpdated += OnPeerUpdated;
        discovery.PeerExpired += OnPeerExpired;
        _discovery = discovery;
        foreach (var configured in ConfiguredPeers)
            EnsureSession(configured.DeviceId, configured.Name).UpdateEndpoint(
                $"{configured.Host}:{configured.Port}", configured.Name);
        try { await discovery.StartAsync(cancellationToken).ConfigureAwait(false); }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"LAN discovery unavailable: {ex.Message}");
            discovery.PeerUpdated -= OnPeerUpdated;
            discovery.PeerExpired -= OnPeerExpired;
            await discovery.DisposeAsync().ConfigureAwait(false);
            _discovery = null;
        }
        _maintenance = MaintainConnectionsAsync(_shutdown.Token);
        if (OperatingSystem.IsLinux()) _displayPublisher = PublishDisplayChangesAsync(_shutdown.Token);
    }

    /// <summary>Accept an inbound TCP transport. TLS identity is checked before any peer state is created.</summary>
    public async Task HandleClientAsync(TcpClient socket, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        using var shutdownRegistration = linked.Token.Register(static state => ((TcpClient)state!).Dispose(), socket);
        try
        {
            await using var tls = await MutualTlsTransport.AcceptAsync(socket, _identity, _trust,
                TimeSpan.FromSeconds(10), linked.Token).ConfigureAwait(false);
            await RunConnectionAsync(tls.PeerDeviceId, Environment.MachineName, tls.Stream, outbound: false,
                expectedLane: null, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"Peer connection ended: {ex.Message}"); }
    }

    private void OnPeerUpdated(DiscoveredPeer peer) => EnsureSession(peer.DeviceId, peer.Name).UpdateEndpoint(
        $"{peer.Address}:{peer.TcpPort}", peer.Name, peer.LastSeen);

    private void OnPeerExpired(DiscoveredPeer peer)
    {
        if (_sessions.TryGetValue(peer.DeviceId, out var session))
        {
            if (!ConfiguredPeers.Any(p => p.DeviceId == peer.DeviceId))
                session.UpdateEndpoint(null, lastSeen: peer.LastSeen, clearEndpoint: true);
            else session.UpdateEndpoint(null, lastSeen: peer.LastSeen);
            SessionChanged?.Invoke(session.Snapshot);
        }
    }

    private PeerSession EnsureSession(string deviceId, string name)
    {
        var session = _sessions.GetOrAdd(deviceId, id =>
        {
            var created = new PeerSession(id, name);
            created.Changed += () => SessionChanged?.Invoke(created.Snapshot);
            return created;
        });
        return session;
    }

    private async Task MaintainConnectionsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var discovered = _discovery?.GetPeers() ?? [];
                var peers = discovered.Concat(ConfiguredPeers
                    .Where(configured => discovered.All(p => p.DeviceId != configured.DeviceId))
                    .Select(configured => new DiscoveredPeer(configured.DeviceId, configured.Name,
                        configured.Host, configured.Port, DateTimeOffset.UtcNow)));
                foreach (var peer in peers)
                {
                    // One deterministic dialer per pair prevents two daemons repeatedly replacing each other's lane.
                    if (string.CompareOrdinal(_identity.DeviceId, peer.DeviceId) >= 0) continue;
                    foreach (var lane in Enum.GetValues<PeerLane>())
                    {
                        var key = $"{peer.DeviceId}|{lane}";
                        if (!_dialers.TryAdd(key, Task.CompletedTask)) continue;
                        var task = DialLoopAsync(peer, lane, token);
                        _dialers[key] = task;
                        _ = task.ContinueWith(completed => { _dialers.TryRemove(key, out var ignored); },
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task DialLoopAsync(DiscoveredPeer peer, PeerLane lane, CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!token.IsCancellationRequested && (_discovery?.GetPeers().Any(p => p.DeviceId == peer.DeviceId) == true ||
               ConfiguredPeers.Any(p => p.DeviceId == peer.DeviceId)))
        {
            try
            {
                var endpoint = _discovery?.GetPeers().FirstOrDefault(p => p.DeviceId == peer.DeviceId);
                endpoint ??= ConfiguredPeers.Where(p => p.DeviceId == peer.DeviceId)
                    .Select(p => new DiscoveredPeer(p.DeviceId, p.Name, p.Host, p.Port, DateTimeOffset.UtcNow))
                    .FirstOrDefault();
                if (endpoint is null) break;
                EnsureSession(endpoint.DeviceId, endpoint.Name).UpdateEndpoint(
                    $"{endpoint.Address}:{endpoint.TcpPort}", endpoint.Name, endpoint.LastSeen);
                var tls = await MutualTlsTransport.ConnectAsync(endpoint.Address, endpoint.TcpPort, _identity, _trust,
                    endpoint.DeviceId, TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                await using (tls.ConfigureAwait(false))
                    await RunConnectionAsync(endpoint.DeviceId, endpoint.Name, tls.Stream, outbound: true, expectedLane: lane, token).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception) { await Task.Delay(delay, token).ConfigureAwait(false); delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2)); }
        }
    }

    private async Task RunConnectionAsync(string deviceId, string peerName, Stream stream, bool outbound,
        PeerLane? expectedLane, CancellationToken token)
    {
        var session = EnsureSession(deviceId, peerName);
        await using var frames = new BinaryFrameConnection(stream, leaveOpen: true);
        await using var interactive = new InteractiveShellManager(deviceId, _permissions, frames.SendAsync);
        await using var streamingShell = new StreamingCommandManager(deviceId, _permissions, frames.SendAsync);
        await using var files = new FileTransferServer(deviceId, _permissions, frames.SendAsync);
        await using var input = _input.CreateSession(deviceId);
        using var clipboardStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task clipboardPublisher = Task.CompletedTask;
        var clipboardStarted = 0;
        void StartClipboardPublisher(PeerLane selected)
        {
            if (selected != PeerLane.Control || Interlocked.Exchange(ref clipboardStarted, 1) != 0) return;
            clipboardPublisher = _dispatcher.Clipboard.RunChangeNotificationsAsync(deviceId, frames.SendAsync, clipboardStop.Token);
        }
        PeerLane? lane = expectedLane;
        var laneGate = new object();
        var managedLane = expectedLane is not null;
        bool attached = false;
        PeerLane SelectLane(ProtocolMessage message)
        {
            lock (laneGate)
            {
                if (lane is not null) return lane.Value;
                if (message.Method == "session.lane.open")
                {
                    lane = ParseLane(message.Payload);
                    managedLane = true;
                }
                else lane = ClassifyLane(message.Method);
                return lane.Value;
            }
        }
        MultiplexedProtocolPeer? protocol = null;
        protocol = new MultiplexedProtocolPeer(frames, async (message, ct) =>
        {
            var selected = SelectLane(message);
            if (managedLane && !attached)
            {
                if (!await session.AttachAsync(selected, protocol!, IsPreferred(outbound, deviceId)).ConfigureAwait(false))
                    throw new IOException("A preferred lane connection is already active.");
                attached = true;
                _lanes.TryAdd(protocol!, selected);
                StartClipboardPublisher(selected);
            }
            if (message.Method == "session.lane.open")
            {
                if (selected == PeerLane.Control)
                    await LoadPeerMetadataAsync(deviceId, session, protocol!, ct).ConfigureAwait(false);
                return new ProtocolMessage(MessageKind.Response, message.RequestId, message.StreamId,
                    message.Method, []);
            }
            return message.Method is ShellExecWire.Open or ShellExecWire.Close
                ? await streamingShell.HandleRequestAsync(message, ct).ConfigureAwait(false)
                : message.Method.StartsWith("shell.", StringComparison.Ordinal) &&
                    message.Method is "shell.open" or "shell.resize" or "shell.close"
                ? await interactive.HandleRequestAsync(message, ct).ConfigureAwait(false)
                : message.Method.StartsWith("file.", StringComparison.Ordinal)
                    ? await files.HandleRequestAsync(message, ct).ConfigureAwait(false)
                    : message.Method is "input.open" or "input.close" or "input.release"
                        ? await input.HandleRequestAsync(message, ct).ConfigureAwait(false)
                        : await _dispatcher.HandleAsync(deviceId, message, frames.SendAsync, ct).ConfigureAwait(false);
        });
        protocol.MessageReceived += message =>
        {
            var selected = SelectLane(message);
            if (message.Method == "display.changed" && message.Kind == MessageKind.Event &&
                message.RequestId == 0 && message.StreamId == 0)
            {
                try
                {
                    var display = JsonSerializer.Deserialize<DisplayMetadata>(message.Payload);
                    if (display is null) session.UpdateMetadata(clearDisplay: true);
                    else session.UpdateMetadata(display: display);
                }
                catch (JsonException) { throw new InvalidDataException("Invalid display.changed payload."); }
                return ValueTask.CompletedTask;
            }
            if (selected == PeerLane.Control && message.Method == "clipboard.changed" && message.Kind == MessageKind.Event)
                return _dispatcher.Clipboard.ApplyChangedEventAsync(deviceId, message, token);
            return message.Method switch
            {
                "shell.input" => interactive.HandleMessageAsync(message),
                ShellExecWire.Stdin => streamingShell.HandleMessageAsync(message),
                "file.put.data" => files.HandleMessageAsync(message),
                "input.event" => input.HandleMessageAsync(message),
                _ => session.OnMessageAsync(selected, message)
            };
        };
        try
        {
            if (expectedLane is { } outboundLane)
            {
                lane = outboundLane;
                if (!await session.AttachAsync(outboundLane, protocol, IsPreferred(outbound, deviceId)).ConfigureAwait(false))
                    throw new IOException("A preferred lane connection is already active.");
                attached = true;
                _lanes.TryAdd(protocol, outboundLane);
                StartClipboardPublisher(outboundLane);
                var hello = await protocol.RequestAsync("session.lane.open", System.Text.Encoding.UTF8.GetBytes(outboundLane.ToString()),
                    cancellationToken: token).ConfigureAwait(false);
                if (hello.Kind != MessageKind.Response) throw new InvalidDataException("Invalid lane handshake response.");
            }
            if (outbound && expectedLane == PeerLane.Control)
                await LoadPeerMetadataAsync(deviceId, session, protocol, token).ConfigureAwait(false);
            await protocol.Completion.ConfigureAwait(false);
        }
        finally
        {
            clipboardStop.Cancel();
            try { await clipboardPublisher.ConfigureAwait(false); }
            catch (OperationCanceledException) when (clipboardStop.IsCancellationRequested) { }
            _lanes.TryRemove(protocol, out _);
            if (attached) session.Detach(lane ?? PeerLane.Control, protocol);
            await protocol.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static PeerLane ClassifyLane(string method) => method.StartsWith("input.", StringComparison.Ordinal)
        ? PeerLane.Realtime
        : method.StartsWith("shell.", StringComparison.Ordinal) ? PeerLane.Interactive
        : method.StartsWith("file.", StringComparison.Ordinal) || method.StartsWith("fs.", StringComparison.Ordinal)
            ? PeerLane.Bulk : PeerLane.Control;

    private static PeerLane ParseLane(byte[] payload)
    {
        if (payload.Length is < 1 or > 32 || !Enum.TryParse<PeerLane>(
                System.Text.Encoding.UTF8.GetString(payload), ignoreCase: true, out var lane) || !Enum.IsDefined(lane))
            throw new InvalidDataException("Invalid session lane handshake.");
        return lane;
    }

    private async Task LoadPeerMetadataAsync(string deviceId, PeerSession session,
        MultiplexedProtocolPeer protocol, CancellationToken token)
    {
        var info = await protocol.RequestAsync("device.info", [], cancellationToken: token).ConfigureAwait(false);
        if (info.Kind != MessageKind.Response) return;
        var device = JsonSerializer.Deserialize<DeviceInfo>(info.Payload);
        session.UpdateMetadata(device: device);
        if (device?.Capabilities.Any(c => c.Capability == Capability.Display && c.Version > 0) != true ||
            !_permissions.IsAllowed(deviceId, Capability.Input)) return;
        try
        {
            var display = await protocol.RequestAsync("display.info", [], cancellationToken: token).ConfigureAwait(false);
            if (display.Kind == MessageKind.Response)
                session.UpdateMetadata(display: JsonSerializer.Deserialize<DisplayMetadata>(display.Payload));
        }
        catch (Exception ex) when (ex is RemoteProtocolException or IOException or UnauthorizedAccessException) { }
    }

    private async Task PublishDisplayChangesAsync(CancellationToken token)
    {
        var service = new LinuxDisplayMetadataService();
        DisplayMetadata? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                DisplayMetadata? current;
                try { current = await service.GetPrimaryDisplayAsync(token).ConfigureAwait(false); }
                catch (Exception) when (!token.IsCancellationRequested) { continue; }
                if (Equals(current, previous)) continue;
                previous = current;
                var payload = JsonSerializer.SerializeToUtf8Bytes(current);
                foreach (var session in _sessions.Values)
                {
                    if (!session.Online) continue;
                    try { await session.SendAsync(PeerLane.Control,
                        new ProtocolMessage(MessageKind.Event, 0, 0, "display.changed", payload), token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private bool IsPreferred(bool outbound, string peerId) =>
        outbound == (string.CompareOrdinal(_identity.DeviceId, peerId) < 0);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        if (_maintenance is not null) { try { await _maintenance.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        if (_displayPublisher is not null) { try { await _displayPublisher.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        if (_discovery is not null)
        {
            _discovery.PeerUpdated -= OnPeerUpdated;
            _discovery.PeerExpired -= OnPeerExpired;
            await _discovery.DisposeAsync().ConfigureAwait(false);
        }
        try { await Task.WhenAll(_dialers.Values).ConfigureAwait(false); } catch (OperationCanceledException) { }
        foreach (var lane in _lanes.Keys) await lane.DisposeAsync().ConfigureAwait(false);
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            SessionRemoved?.Invoke(session.DeviceId);
        }
        _sessions.Clear();
        _shutdown.Dispose();
    }
}
