using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.LocalIpc;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Services;
using Xas.Daemon.Pairing;
using Xas.Daemon.Sessions;
using Xas.Daemon.Clipboard;
using Xas.Daemon.FileSystem.Mount;

namespace Xas.Daemon.LocalIpc;

/// <summary>Per-user command endpoint. It exposes trusted daemon state without exposing network credentials.</summary>
internal sealed class LocalIpcServer(PeerSessionManager sessions, PairingService pairing,
    LocalConfiguration configuration, PeerTrustStore trust, PeerPermissionStore permissions,
    ClipboardService clipboard, PeerAdministrationService peerAdministration, RemoteMountManager? remoteMounts,
    Func<string> uiUrl)
{
    private readonly PeerSessionManager _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly PairingService _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
    private readonly LocalConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly PeerTrustStore _trust = trust ?? throw new ArgumentNullException(nameof(trust));
    private readonly PeerPermissionStore _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    private readonly ClipboardService _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    private readonly PeerAdministrationService _peerAdministration = peerAdministration ?? throw new ArgumentNullException(nameof(peerAdministration));
    private readonly RemoteMountManager? _remoteMounts = remoteMounts;
    private readonly Func<string> _uiUrl = uiUrl ?? throw new ArgumentNullException(nameof(uiUrl));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) await RunPipesAsync(cancellationToken).ConfigureAwait(false);
        else if (OperatingSystem.IsLinux()) await RunUnixSocketAsync(cancellationToken).ConfigureAwait(false);
        else throw new PlatformNotSupportedException("Local daemon IPC requires Windows or Linux.");
    }

    private async Task RunPipesAsync(CancellationToken token)
    {
        var clients = new ConcurrentBag<Task>();
        while (!token.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(LocalIpcEndpoint.PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(token).ConfigureAwait(false); }
            catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
            clients.Add(HandleConnectionAsync(pipe, token));
        }
        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private async Task RunUnixSocketAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var path = LocalIpcEndpoint.SocketPath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (File.Exists(path)) File.Delete(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(64);
        using var registration = token.Register(static state => ((Socket)state!).Dispose(), listener);
        var clients = new ConcurrentBag<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(token).ConfigureAwait(false);
                clients.Add(HandleConnectionAsync(new NetworkStream(socket, ownsSocket: true), token));
            }
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken shutdown)
    {
        await using var frames = new BinaryFrameConnection(stream, leaveOpen: true);
        var openShells = new ConcurrentDictionary<uint, ForwardedShell>();
        var boundShellStreams = new BoundShellStreams(frames.SendAsync);
        PeerSession? boundSession = null;
        Func<PeerSession, PeerLane, ProtocolMessage, ValueTask>? boundHandler = null;
        uint nextShellId = 0;
        await using var peer = new MultiplexedProtocolPeer(frames, async (request, token) =>
        {
            try
            {
                if (request.Method == LocalIpcProtocol.Bind)
                {
                    if (boundSession is not null) throw new InvalidOperationException("This local IPC connection is already bound to a device.");
                    var target = Resolve(ReadTarget(request.Payload).DeviceId) ?? throw new InvalidOperationException("No matching paired device.");
                    boundSession = _sessions.GetSession(target.DeviceId) ?? throw new IOException($"Device {target.DeviceId} is offline.");
                    boundHandler = async (_, lane, message) =>
                    {
                        if (lane == PeerLane.Interactive && IsShellOutput(message))
                        {
                            try { await boundShellStreams.OnMessageAsync(message).ConfigureAwait(false); }
                            catch (Exception) { /* A local IPC subscriber cannot terminate the shared peer session. */ }
                            return;
                        }
                        await TrySendLocalAsync(frames.SendAsync, message).ConfigureAwait(false);
                    };
                    boundSession.MessageReceived += boundHandler;
                    return Response(request, []);
                }
                if (request.Method == LocalIpcProtocol.ShellOpen)
                {
                    var open = JsonSerializer.Deserialize<LocalShellOpen>(request.Payload, LocalIpcProtocol.Json)
                        ?? throw new InvalidDataException("Invalid shell open request.");
                    var target = Resolve(open.DeviceId) ?? throw new InvalidOperationException("No matching paired device.");
                    var session = _sessions.GetSession(target.DeviceId) ?? throw new IOException($"Device {target.DeviceId} is offline.");
                    var localId = unchecked(Interlocked.Increment(ref nextShellId));
                    if (localId == 0) localId = unchecked(Interlocked.Increment(ref nextShellId));
                    var forwarded = new ForwardedShell(session, localId, frames.SendAsync);
                    session.MessageReceived += forwarded.OnRemoteMessageAsync;
                    try
                    {
                        var response = await session.RequestAsync(PeerLane.Interactive, ShellExecWire.Open,
                            ShellWire.EncodeRequest(open.Request), token).ConfigureAwait(false);
                        var remoteId = ShellExecWire.DecodeSessionId(response.Payload);
                        await forwarded.SetRemoteId(remoteId).ConfigureAwait(false);
                        openShells[localId] = forwarded;
                        return new ProtocolMessage(MessageKind.Response, request.RequestId, localId,
                            request.Method, ShellExecWire.EncodeSessionId(localId));
                    }
                    catch { session.MessageReceived -= forwarded.OnRemoteMessageAsync; throw; }
                }
                if (request.Method == LocalIpcProtocol.ShellClose)
                {
                    var localId = ShellExecWire.DecodeSessionId(request.Payload);
                    if (openShells.TryRemove(localId, out var forwarded))
                    {
                        await forwarded.CloseAsync(token).ConfigureAwait(false);
                        forwarded.Session.MessageReceived -= forwarded.OnRemoteMessageAsync;
                        await forwarded.DisposeAsync().ConfigureAwait(false);
                    }
                    else if (boundSession is not null)
                    {
                        var closed = await boundSession.RequestAsync(PeerLane.Interactive, ShellExecWire.Close,
                            request.Payload, token).ConfigureAwait(false);
                        await boundShellStreams.RemoveAsync(localId, exec: true).ConfigureAwait(false);
                        return closed;
                    }
                    return new ProtocolMessage(MessageKind.Response, request.RequestId, request.StreamId,
                        request.Method, []);
                }
                if (boundSession is not null && !request.Method.StartsWith("local.", StringComparison.Ordinal))
                {
                    var isShellOpen = request.Method is ShellExecWire.Open or "shell.open";
                    if (isShellOpen) await boundShellStreams.BeginOpenAsync().ConfigureAwait(false);
                    try
                    {
                        var response = await boundSession.RequestAsync(LaneFor(request.Method), request.Method,
                            request.Payload, token).ConfigureAwait(false);
                        if (isShellOpen)
                        {
                            var remoteId = ShellExecWire.DecodeSessionId(response.Payload);
                            var exec = request.Method == ShellExecWire.Open;
                            var closeMethod = exec ? ShellExecWire.Close : "shell.close";
                            await boundShellStreams.CompleteOpenAsync(remoteId, exec, closeMethod).ConfigureAwait(false);
                        }
                        else if (request.Method == "shell.close")
                            await boundShellStreams.RemoveAsync(ShellExecWire.DecodeSessionId(request.Payload), exec: false).ConfigureAwait(false);
                        return response;
                    }
                    finally
                    {
                        if (isShellOpen) await boundShellStreams.EndOpenAsync().ConfigureAwait(false);
                    }
                }
                return await DispatchRequestAsync(request, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
                KeyNotFoundException or JsonException or ArgumentException or NotSupportedException or
                UnauthorizedAccessException or RemoteProtocolException)
            {
                return new ProtocolMessage(MessageKind.Error, request.RequestId, request.StreamId,
                    request.Method, Encoding.UTF8.GetBytes(ex.Message));
            }
        });
        peer.MessageReceived += async message =>
        {
            if (message.Method == ShellExecWire.Stdin && openShells.TryGetValue(message.StreamId, out var shell))
                await shell.SendStdinAsync(message, shutdown).ConfigureAwait(false);
            else if (boundSession is not null)
                await boundSession.SendAsync(LaneFor(message.Method), message, shutdown).ConfigureAwait(false);
        };
        try { await peer.Completion.ConfigureAwait(false); }
        catch (Exception) { }
        finally
        {
            if (boundSession is not null && boundHandler is not null) boundSession.MessageReceived -= boundHandler;
            if (boundSession is not null)
                await boundShellStreams.CloseAllAsync(boundSession).ConfigureAwait(false);
            foreach (var item in openShells.Values)
            {
                item.Session.MessageReceived -= item.OnRemoteMessageAsync;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    try { await item.CloseAsync(timeout.Token).ConfigureAwait(false); } catch { }
                await item.DisposeAsync().ConfigureAwait(false);
            }
            openShells.Clear();
            await boundShellStreams.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolMessage> DispatchRequestAsync(ProtocolMessage request, CancellationToken token)
    {
        switch (request.Method)
        {
            case LocalIpcProtocol.Devices:
            {
                var online = _sessions.GetSnapshots().ToDictionary(s => s.DeviceId, StringComparer.Ordinal);
                var devices = _configuration.Peers.Select(p => online.TryGetValue(p.DeviceId, out var snapshot) && snapshot.Device is not null
                    ? new KnownDeviceInfo(p.DeviceId, snapshot.Device.Name, snapshot.Device.Os, snapshot.Device.Architecture,
                        snapshot.Device.Capabilities, snapshot.Online, snapshot.Endpoint, snapshot.LastSeen, snapshot.RoundTripTime?.TotalMilliseconds)
                    : new KnownDeviceInfo(p.DeviceId, p.Name, "Unknown", "Unknown", Array.Empty<CapabilityVersion>(),
                        snapshot?.Online ?? false, snapshot?.Endpoint ?? $"{p.Host}:{p.Port}", snapshot?.LastSeen,
                        snapshot?.RoundTripTime?.TotalMilliseconds)).ToArray();
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(devices, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.Info:
            {
                var target = ReadTarget(request.Payload);
                var configured = Resolve(target.DeviceId);
                if (configured is null) return Response(request, JsonSerializer.SerializeToUtf8Bytes<DeviceInfo?>(null, LocalIpcProtocol.Json));
                var snapshot = _sessions.GetSnapshots().FirstOrDefault(s => s.DeviceId == configured.DeviceId);
                var info = snapshot?.Device;
                if (info is null && _sessions.GetSession(configured.DeviceId) is { } infoSession)
                    info = JsonSerializer.Deserialize<DeviceInfo>((await infoSession.RequestAsync(PeerLane.Control, "device.info", [], token).ConfigureAwait(false)).Payload);
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(info, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.SetDefault:
                _configuration.SetDefault(ReadTarget(request.Payload).DeviceId ?? throw new InvalidDataException("Device ID is required."));
                return Response(request, []);
            case LocalIpcProtocol.Ping:
            {
                var configured = Resolve(ReadTarget(request.Payload).DeviceId);
                if (configured is null || _sessions.GetSession(configured.DeviceId) is not { } pingSession)
                    return Response(request, [0]);
                try
                {
                    var ping = await pingSession.RequestAsync(PeerLane.Control, "device.ping", [], token).ConfigureAwait(false);
                    return Response(request, [ping.Kind == MessageKind.Response ? (byte)1 : (byte)0]);
                }
                catch (IOException) { return Response(request, [0]); }
            }
            case LocalIpcProtocol.ClipboardPush:
            {
                var target = Resolve(ReadTarget(request.Payload).DeviceId)
                    ?? throw new InvalidOperationException("No matching paired device.");
                var session = _sessions.GetSession(target.DeviceId)
                    ?? throw new IOException($"Device {target.DeviceId} is offline.");
                var local = await _clipboard.HandleAsync(target.DeviceId,
                    new ProtocolMessage(MessageKind.Request, 1, 0, "clipboard.get", []), token).ConfigureAwait(false);
                var remote = await session.RequestAsync(PeerLane.Control, "clipboard.set", local.Payload, token).ConfigureAwait(false);
                if (remote.Payload.Length != 0) throw new InvalidDataException("Invalid remote clipboard.set response.");
                return Response(request, []);
            }
            case LocalIpcProtocol.ClipboardPull:
            {
                var target = Resolve(ReadTarget(request.Payload).DeviceId)
                    ?? throw new InvalidOperationException("No matching paired device.");
                var session = _sessions.GetSession(target.DeviceId)
                    ?? throw new IOException($"Device {target.DeviceId} is offline.");
                var remote = await session.RequestAsync(PeerLane.Control, "clipboard.get", [], token).ConfigureAwait(false);
                await _clipboard.ApplyChangedEventAsync(target.DeviceId,
                    new ProtocolMessage(MessageKind.Event, 0, 0, "clipboard.changed", remote.Payload), token).ConfigureAwait(false);
                return Response(request, []);
            }
            case LocalIpcProtocol.ClipboardStatus:
            {
                var target = Resolve(ReadTarget(request.Payload).DeviceId)
                    ?? throw new InvalidOperationException("No matching paired device.");
                var session = _sessions.GetSession(target.DeviceId);
                if (session is null || !session.Online)
                    return Response(request, JsonSerializer.SerializeToUtf8Bytes(
                        new LocalClipboardStatus(false, $"{target.Name} is offline."), LocalIpcProtocol.Json));
                if (!_permissions.IsAllowed(target.DeviceId, Capability.Clipboard) || !_clipboard.IsAvailable)
                    return Response(request, JsonSerializer.SerializeToUtf8Bytes(
                        new LocalClipboardStatus(false, "Local clipboard access is unavailable or not granted for this peer."), LocalIpcProtocol.Json));
                try
                {
                    _ = await session.RequestAsync(PeerLane.Control, "clipboard.get", [], token).ConfigureAwait(false);
                    return Response(request, JsonSerializer.SerializeToUtf8Bytes(
                        new LocalClipboardStatus(true, $"Background clipboard sync with {target.Name} is active."), LocalIpcProtocol.Json));
                }
                catch (Exception ex) when (ex is IOException or RemoteProtocolException or PlatformNotSupportedException)
                {
                    return Response(request, JsonSerializer.SerializeToUtf8Bytes(
                        new LocalClipboardStatus(false, $"Clipboard sync is unavailable: {ex.Message}"), LocalIpcProtocol.Json));
                }
            }
            case LocalIpcProtocol.StorageVolumes:
            {
                var manager = RequireRemoteMounts();
                var target = ReadTarget(request.Payload);
                var volumes = await manager.GetRemoteVolumesAsync(target.DeviceId, token).ConfigureAwait(false);
                var response = volumes.Select(ToLocalVolumeInfo).ToArray();
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(response, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.StorageMounts:
            {
                var manager = RequireRemoteMounts();
                var target = ReadTarget(request.Payload);
                var resolvedDevice = target.DeviceId is null ? null : Resolve(target.DeviceId)?.DeviceId
                    ?? throw new InvalidOperationException("No matching paired device.");
                var response = manager.GetSnapshots()
                    .Where(m => resolvedDevice is null || m.DeviceId == resolvedDevice)
                    .Select(m => new LocalRemoteVolumeInfo(m.DeviceId, m.DeviceName, m.VolumeId, m.VolumeName,
                        m.Kind, m.ReadOnly, m.TotalBytes, m.FreeBytes, m.FileSystem, m.MountPoint, false))
                    .ToArray();
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(response, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.StorageMount:
            {
                var manager = RequireRemoteMounts();
                var target = ReadStorageTarget(request.Payload);
                var mounted = await manager.MountVolumeAsync(target.DeviceId, target.Volume, token).ConfigureAwait(false);
                var response = new LocalRemoteVolumeInfo(mounted.DeviceId, mounted.DeviceName, mounted.VolumeId,
                    mounted.VolumeName, mounted.Kind, mounted.ReadOnly, mounted.TotalBytes, mounted.FreeBytes,
                    mounted.FileSystem, mounted.MountPoint, false);
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(response, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.StorageUnmount:
            {
                var manager = RequireRemoteMounts();
                var target = ReadStorageTarget(request.Payload);
                var unmounted = await manager.UnmountVolumeAsync(target.DeviceId, target.Volume, token).ConfigureAwait(false);
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(unmounted, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.StorageEject:
            {
                var manager = RequireRemoteMounts();
                var target = ReadStorageTarget(request.Payload);
                await manager.EjectVolumeAsync(target.DeviceId, target.Volume, token).ConfigureAwait(false);
                return Response(request, []);
            }
            case LocalIpcProtocol.UiInfo:
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(
                    new LocalUiInfo(_uiUrl()), LocalIpcProtocol.Json));
            case LocalIpcProtocol.Revoke:
            {
                var target = ReadTarget(request.Payload).DeviceId
                    ?? throw new InvalidDataException("Device ID is required.");
                var revoked = await _peerAdministration.RevokeAsync(target).ConfigureAwait(false);
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(revoked, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.Endpoint:
            {
                var update = JsonSerializer.Deserialize<LocalEndpointUpdate>(request.Payload, LocalIpcProtocol.Json)
                    ?? throw new InvalidDataException("Invalid endpoint update.");
                if (string.IsNullOrWhiteSpace(update.Host) || update.Port is < 1 or > 65535)
                    throw new InvalidDataException("Endpoint host and port are invalid.");
                var configured = Resolve(update.DeviceId) ?? throw new InvalidOperationException("No matching paired device.");
                _configuration.UpsertPeer(configured with { Host = update.Host, Port = update.Port });
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(configured.DeviceId, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.PairBegin:
            {
                var begin = JsonSerializer.Deserialize<LocalPairBegin>(request.Payload, LocalIpcProtocol.Json)
                    ?? throw new InvalidDataException("Invalid pair request.");
                if (begin.ControlPort is < 1 or >= 65535) throw new InvalidDataException("Invalid control port for pairing.");
                var pending = await _pairing.BeginAsync(begin.Host, checked(begin.ControlPort + 1), begin.ExpectedDeviceId, token).ConfigureAwait(false);
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(pending, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.PairCandidates:
            {
                var trusted = _trust.List().Select(p => p.DeviceId).ToHashSet(StringComparer.Ordinal);
                var candidates = _sessions.GetDiscoveredPeers()
                    .Where(p => !trusted.Contains(p.DeviceId))
                    .Select(p => new LocalPairCandidate(p.DeviceId, p.Name, p.Address, p.TcpPort, p.LastSeen))
                    .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.DeviceId, StringComparer.Ordinal)
                    .ToArray();
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(candidates, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.PairDiscover:
            {
                var target = JsonSerializer.Deserialize<LocalPairTarget>(request.Payload, LocalIpcProtocol.Json)
                    ?? throw new InvalidDataException("Invalid discovered pair request.");
                var candidate = ResolvePairCandidate(target.Query);
                if (candidate.TcpPort >= 65535) throw new InvalidOperationException("The discovered device cannot expose the pairing port.");
                var pending = await _pairing.BeginAsync(candidate.Address, checked(candidate.TcpPort + 1), candidate.DeviceId, token).ConfigureAwait(false);
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(pending, LocalIpcProtocol.Json));
            }
            case LocalIpcProtocol.PairList:
                return Response(request, JsonSerializer.SerializeToUtf8Bytes(_pairing.ListPending(), LocalIpcProtocol.Json));
            case LocalIpcProtocol.PairApprove:
            {
                var decision = JsonSerializer.Deserialize<LocalPairDecision>(request.Payload, LocalIpcProtocol.Json)
                    ?? throw new InvalidDataException("Invalid pair decision.");
                var pending = _pairing.ListPending().FirstOrDefault(p => p.PairingId == decision.PairingId)
                    ?? throw new KeyNotFoundException("Pairing request not found or expired.");
                var paired = await _pairing.ApproveAsync(decision.PairingId, decision.Approve, token).ConfigureAwait(false);
                if (decision.Approve && !paired)
                    throw new InvalidOperationException("The other device rejected the pairing request or the pairing session failed.");
                if (paired)
                {
                    if (string.IsNullOrWhiteSpace(pending.Address) || pending.ControlPort is < 1 or >= 65535)
                        throw new InvalidDataException("Paired device did not provide a usable control endpoint.");
                    _configuration.UpsertPeer(new ConfiguredPeer(pending.DeviceId, pending.DisplayName, pending.Address, pending.ControlPort));
                    PairPermissionPresets.Apply(_permissions, pending.DeviceId, decision.Preset);
                }
                _sessions.UpdateConfiguredPeers(_configuration.Peers);
                return Response(request, []);
            }
            default:
                throw new InvalidDataException($"Unknown local command: {request.Method}");
        }
    }

    private ConfiguredPeer? Resolve(string? deviceId) => _configuration.Resolve(deviceId);

    private RemoteMountManager RequireRemoteMounts() => _remoteMounts
        ?? throw new PlatformNotSupportedException("Native remote filesystem mounts are unavailable on this platform.");

    private static LocalRemoteVolumeInfo ToLocalVolumeInfo(RemoteVolumeStatus item) =>
        new(item.DeviceId, item.DeviceName, item.Volume.Id, item.Volume.Name, item.Volume.Kind,
            item.Volume.ReadOnly, item.Volume.TotalBytes, item.Volume.FreeBytes, item.Volume.FileSystem,
            item.MountedAt, item.AutoMountSuppressed);

    private Xas.Core.Discovery.DiscoveredPeer ResolvePairCandidate(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidDataException("Pairing target is required.");
        var trusted = _trust.List().Select(p => p.DeviceId).ToHashSet(StringComparer.Ordinal);
        var candidates = _sessions.GetDiscoveredPeers().Where(p => !trusted.Contains(p.DeviceId)).ToArray();
        var exactId = candidates.FirstOrDefault(p => string.Equals(p.DeviceId, query, StringComparison.Ordinal));
        if (exactId is not null) return exactId;
        var prefix = candidates.Where(p => p.DeviceId.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (prefix.Length == 1) return prefix[0];
        var byName = candidates.Where(p => string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byName.Length == 1) return byName[0];
        if (prefix.Length > 1 || byName.Length > 1) throw new InvalidOperationException($"Pairing target '{query}' is ambiguous.");
        throw new InvalidOperationException($"No unpaired discovered device matches '{query}'.");
    }

    private static PeerLane LaneFor(string method) => method.StartsWith("input.", StringComparison.Ordinal)
        ? PeerLane.Realtime
        : method.StartsWith("file.", StringComparison.Ordinal) ? PeerLane.Bulk
        : method.StartsWith("shell.", StringComparison.Ordinal) ? PeerLane.Interactive
        : PeerLane.Control;

    private static bool IsShellOutput(ProtocolMessage message) =>
        message.Method is (ShellExecWire.Stdout or ShellExecWire.Stderr or ShellExecWire.Exit or ShellExecWire.Error or
            "shell.output" or "shell.exit") &&
        message.Kind is (MessageKind.StreamData or MessageKind.StreamEnd or MessageKind.Event);

    private static async ValueTask TrySendLocalAsync(
        Func<ProtocolMessage, CancellationToken, ValueTask> sendLocal, ProtocolMessage message)
    {
        try { await sendLocal(message, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static LocalTarget ReadTarget(byte[] payload) => payload.Length == 0
        ? new LocalTarget(null)
        : JsonSerializer.Deserialize<LocalTarget>(payload, LocalIpcProtocol.Json) ?? throw new InvalidDataException("Invalid device target.");

    private static LocalStorageVolumeTarget ReadStorageTarget(byte[] payload) =>
        JsonSerializer.Deserialize<LocalStorageVolumeTarget>(payload, LocalIpcProtocol.Json)
        ?? throw new InvalidDataException("Invalid storage volume target.");

    private static ProtocolMessage Response(ProtocolMessage request, byte[] payload) =>
        new(MessageKind.Response, request.RequestId, request.StreamId, request.Method, payload);

    internal sealed class BoundShellStreams(
        Func<ProtocolMessage, CancellationToken, ValueTask> sendLocal) : IAsyncDisposable
    {
        private const int MaxEarlyBytes = 1024 * 1024;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<BoundShellKey, string> _remoteIds = [];
        private readonly Queue<ProtocolMessage> _early = new();
        private readonly HashSet<BoundShellKey> _overflowed = [];
        private int _opening;
        private int _earlyBytes;

        public async Task BeginOpenAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { _opening++; }
            finally { _gate.Release(); }
        }

        public async Task CompleteOpenAsync(uint remoteId, bool exec, string closeMethod)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var key = new BoundShellKey(exec, remoteId);
                _remoteIds[key] = closeMethod;
                var unmatched = new Queue<ProtocolMessage>();
                while (_early.TryDequeue(out var message))
                {
                    if (KeyFor(message) == key) await TrySendLocalAsync(sendLocal, message).ConfigureAwait(false);
                    else unmatched.Enqueue(message);
                }
                while (unmatched.TryDequeue(out var message)) _early.Enqueue(message);
                _earlyBytes = _early.Sum(message => message.Payload.Length);
                if (_overflowed.Remove(key))
                {
                    var failure = exec
                        ? new ProtocolMessage(MessageKind.Event, 0, remoteId, ShellExecWire.Error,
                            Encoding.UTF8.GetBytes("Too much shell output arrived before the open response."))
                        : new ProtocolMessage(MessageKind.Event, 0, remoteId, "shell.exit",
                            ShellExecWire.EncodeExitCode(-1));
                    await TrySendLocalAsync(sendLocal, failure).ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }
        }

        public async Task EndOpenAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _opening--;
                if (_opening == 0) { _early.Clear(); _earlyBytes = 0; }
            }
            finally { _gate.Release(); }
        }

        public async ValueTask OnMessageAsync(ProtocolMessage message)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var key = KeyFor(message);
                if (_remoteIds.ContainsKey(key))
                {
                    await TrySendLocalAsync(sendLocal, message).ConfigureAwait(false);
                    return;
                }
                if (_opening == 0) return;
                _earlyBytes = checked(_earlyBytes + message.Payload.Length);
                if (_earlyBytes > MaxEarlyBytes)
                {
                    foreach (var pending in _early) MarkOverflow(KeyFor(pending));
                    _early.Clear();
                    _earlyBytes = 0;
                    MarkOverflow(key);
                    return;
                }
                _early.Enqueue(message);
            }
            finally { _gate.Release(); }
        }

        public async Task RemoveAsync(uint remoteId, bool exec)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { _remoteIds.Remove(new BoundShellKey(exec, remoteId)); }
            finally { _gate.Release(); }
        }

        public async Task CloseAllAsync(PeerSession session)
        {
            KeyValuePair<BoundShellKey, string>[] ids;
            await _gate.WaitAsync().ConfigureAwait(false);
            try { ids = _remoteIds.ToArray(); _remoteIds.Clear(); }
            finally { _gate.Release(); }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            foreach (var item in ids)
            {
                try
                {
                    _ = await session.RequestAsync(PeerLane.Interactive, item.Value,
                        ShellExecWire.EncodeSessionId(item.Key.Id), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception) { }
            }
        }

        private static BoundShellKey KeyFor(ProtocolMessage message) =>
            new(message.Method is ShellExecWire.Stdout or ShellExecWire.Stderr or ShellExecWire.Exit or ShellExecWire.Error,
                message.StreamId);

        private void MarkOverflow(BoundShellKey key)
        {
            if (_overflowed.Count < 64) _overflowed.Add(key);
        }

        internal readonly record struct BoundShellKey(bool Exec, uint Id);

        public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ForwardedShell(PeerSession session, uint localId,
        Func<ProtocolMessage, CancellationToken, ValueTask> sendLocal) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Queue<ProtocolMessage> _early = new();
        private uint _remoteId;
        private int _earlyBytes;

        public PeerSession Session { get; } = session;

        public async ValueTask OnRemoteMessageAsync(PeerSession _, PeerLane lane, ProtocolMessage message)
        {
            if (lane != PeerLane.Interactive || message.Method is not (ShellExecWire.Stdout or ShellExecWire.Stderr or ShellExecWire.Exit or ShellExecWire.Error)) return;
            var entered = false;
            try
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                entered = true;
                if (_remoteId == 0)
                {
                    if (message.StreamId == 0) return;
                    _earlyBytes = checked(_earlyBytes + message.Payload.Length);
                    if (_earlyBytes > 1024 * 1024) throw new InvalidDataException("Too much shell output arrived before the open response.");
                    _early.Enqueue(message);
                    return;
                }
                if (message.StreamId != _remoteId) return;
                await ForwardAsync(message).ConfigureAwait(false);
            }
            catch (Exception) { /* A disconnected local subscriber cannot terminate the shared peer session. */ }
            finally { if (entered) _gate.Release(); }
        }

        public async Task SetRemoteId(uint remoteId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _remoteId = remoteId;
                while (_early.TryDequeue(out var message))
                    if (message.StreamId == remoteId) await ForwardAsync(message).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public ValueTask SendStdinAsync(ProtocolMessage message, CancellationToken token)
        {
            if (message.Kind is not (MessageKind.StreamData or MessageKind.StreamEnd))
                throw new InvalidDataException("Unexpected local shell input message.");
            return Session.SendAsync(PeerLane.Interactive, message with { StreamId = _remoteId }, token);
        }

        public async Task CloseAsync(CancellationToken token) =>
            _ = await Session.RequestAsync(PeerLane.Interactive, ShellExecWire.Close,
                ShellExecWire.EncodeSessionId(_remoteId), token).ConfigureAwait(false);

        private ValueTask ForwardAsync(ProtocolMessage message) => TrySendLocalAsync(sendLocal,
            message with { RequestId = 0, StreamId = localId });

        public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
    }
}
