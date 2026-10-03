using System.Collections.Concurrent;
using System.Threading.Channels;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.FileSystem;
using Xas.Core.Protocol;
using Xas.Daemon.Sessions;

namespace Xas.Daemon.FileSystem.Mount;

public sealed record RemoteMountSnapshot(string DeviceId, string DeviceName, string VolumeId, string VolumeName,
    string Kind, string? MountPoint, bool ReadOnly, long? TotalBytes, long? FreeBytes, string? FileSystem);
public sealed record RemoteVolumeStatus(string DeviceId, string DeviceName, RemoteVolume Volume,
    string? MountedAt, bool AutoMountSuppressed);

/// <summary>
/// Reconciles remote main and removable volumes with native local mounts. The daemon owns this for its entire
/// lifetime so a CLI process is never responsible for keeping a drive letter alive.
/// </summary>
public sealed class RemoteMountManager : IAsyncDisposable
{
    private readonly LocalConfiguration _configuration;
    private readonly PeerSessionManager _sessions;
    private readonly Func<IRemoteFileSystemMountAdapter> _adapterFactory;
    private readonly Func<string, string, RemoteVolume, string> _mountPointFactory;
    private readonly Func<bool> _availabilityProbe;
    private readonly Func<PeerSession, CancellationToken, ValueTask<RemoteVolume[]>> _volumeProvider;
    private readonly Func<PeerSession, string, CancellationToken, ValueTask> _ejectVolume;
    private readonly Action<string>? _log;
    private readonly RemoteMountPaths _mountPaths = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "xas"));
    private readonly ConcurrentDictionary<MountKey, MountedVolume> _mounted = new();
    // Explicit mounts are user intent and must survive automatic removable-volume policy changes.
    private readonly ConcurrentDictionary<MountKey, byte> _manualMounts = new();
    private readonly ConcurrentDictionary<MountKey, byte> _suppressed = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private int _started;
    private int _disposed;

    public RemoteMountManager(LocalConfiguration configuration, PeerSessionManager sessions,
        Func<IRemoteFileSystemMountAdapter>? adapterFactory = null,
        Func<PeerSession, CancellationToken, ValueTask<RemoteVolume[]>>? volumeProvider = null,
        Func<PeerSession, string, CancellationToken, ValueTask>? ejectVolume = null,
        Func<string, string, RemoteVolume, string>? mountPointFactory = null,
        Func<bool>? availabilityProbe = null,
        Action<string>? log = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _adapterFactory = adapterFactory ?? CreatePlatformAdapter;
        _volumeProvider = volumeProvider ?? RemoteFileSystemOperationsClient.GetVolumesAsync;
        _ejectVolume = ejectVolume ?? RemoteFileSystemOperationsClient.EjectVolumeAsync;
        _mountPointFactory = mountPointFactory ?? DefaultMountPoint;
        _availabilityProbe = availabilityProbe ?? DefaultAvailabilityProbe;
        _log = log;
    }

    public bool NativeMountsAvailable => _availabilityProbe();

    public IReadOnlyList<RemoteMountSnapshot> GetSnapshots() => _mounted.Values
        .Select(item => new RemoteMountSnapshot(item.Key.DeviceId, item.DeviceName, item.Key.VolumeId,
            item.Volume.Name, item.Volume.Kind, item.Adapter.MountedAt, item.Volume.ReadOnly, item.Volume.TotalBytes,
            item.Volume.FreeBytes, item.Volume.FileSystem))
        .OrderBy(item => item.DeviceName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.VolumeName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async ValueTask<IReadOnlyList<RemoteVolumeStatus>> GetRemoteVolumesAsync(string? deviceId,
        CancellationToken cancellationToken)
    {
        var peer = ResolvePeer(deviceId);
        var session = RequireReadySession(peer);
        var volumes = await _volumeProvider(session, cancellationToken).ConfigureAwait(false);
        return volumes.Select(volume =>
        {
            var key = new MountKey(peer.DeviceId, volume.Id);
            _mounted.TryGetValue(key, out var mounted);
            return new RemoteVolumeStatus(peer.DeviceId, session.Snapshot.Name, volume,
                mounted?.Adapter.MountedAt, _suppressed.ContainsKey(key));
        }).OrderBy(item => item.Volume.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async ValueTask<RemoteMountSnapshot> MountVolumeAsync(string? deviceId, string volumeQuery,
        CancellationToken cancellationToken)
    {
        var peer = ResolvePeer(deviceId);
        var session = RequireReadySession(peer);
        var volumes = await _volumeProvider(session, cancellationToken).ConfigureAwait(false);
        var volume = ResolveVolume(volumes, volumeQuery);
        var key = new MountKey(peer.DeviceId, volume.Id);
        // Publish intent before awaiting the native mount so a concurrent reconciliation cannot
        // mistake this explicitly requested volume for an automatic mount and tear it down.
        _manualMounts[key] = 0;
        _suppressed.TryRemove(key, out _);
        try
        {
            if (!_mounted.ContainsKey(key))
                await MountAsync(key, session.Snapshot.Name, session, volume, cancellationToken).ConfigureAwait(false);
            if (!_mounted.TryGetValue(key, out var mounted))
                throw new PlatformNotSupportedException("The native remote filesystem mount provider is unavailable.");
            return ToSnapshot(mounted);
        }
        catch
        {
            if (!_mounted.ContainsKey(key)) _manualMounts.TryRemove(key, out _);
            throw;
        }
    }

    public async ValueTask<bool> UnmountVolumeAsync(string? deviceId, string volumeQuery,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = ResolvePeer(deviceId);
        var session = _sessions.GetSession(peer.DeviceId);
        var mounted = _mounted.Values.Where(m => m.Key.DeviceId == peer.DeviceId)
            .Where(m => MatchesVolume(m.Volume, volumeQuery)).ToArray();
        if (mounted.Length > 1) throw new InvalidOperationException($"Remote volume '{volumeQuery}' is ambiguous.");
        if (mounted.Length == 0)
        {
            if (session is null || !session.Online || !session.BulkReady) return false;
            var volumes = await _volumeProvider(session, cancellationToken).ConfigureAwait(false);
            var volume = ResolveVolume(volumes, volumeQuery);
            _suppressed[new MountKey(peer.DeviceId, volume.Id)] = 0;
            return false;
        }
        var key = mounted[0].Key;
        _suppressed[key] = 0;
        _manualMounts.TryRemove(key, out _);
        await UnmountAsync(key).ConfigureAwait(false);
        return true;
    }

    public async ValueTask EjectVolumeAsync(string? deviceId, string volumeQuery,
        CancellationToken cancellationToken)
    {
        var peer = ResolvePeer(deviceId);
        var session = RequireReadySession(peer);
        var volumes = await _volumeProvider(session, cancellationToken).ConfigureAwait(false);
        var volume = ResolveVolume(volumes, volumeQuery);
        if (!string.Equals(volume.Kind, "removable", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only removable remote volumes can be ejected.");
        var key = new MountKey(peer.DeviceId, volume.Id);
        _suppressed[key] = 0;
        _manualMounts.TryRemove(key, out _);
        if (_mounted.ContainsKey(key)) await UnmountAsync(key).ConfigureAwait(false);
        try
        {
            await _ejectVolume(session, volume.Id, cancellationToken).ConfigureAwait(false);
            // UDisks returns after the local mount has been removed. Refresh immediately instead of
            // waiting for the periodic fs.volumes.changed publisher so suppression can clear promptly.
            Queue(peer.DeviceId);
        }
        catch
        {
            _suppressed.TryRemove(key, out _);
            Queue(peer.DeviceId);
            throw;
        }
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Remote mount manager is already running.");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _sessions.SessionChanged += OnSessionChanged;
        _sessions.SessionRemoved += OnSessionRemoved;
        _sessions.RemoteVolumesChanged += OnRemoteVolumesChanged;
        _configuration.Changed += OnConfigurationChanged;
        foreach (var snapshot in _sessions.GetSnapshots()) Queue(snapshot.DeviceId);

        _loop = RunLoopAsync(cancellationToken);
        return _loop;
    }

    private void OnSessionChanged(PeerSessionSnapshot snapshot) => Queue(snapshot.DeviceId);

    private void OnSessionRemoved(string deviceId)
    {
        Queue(deviceId);
    }

    private void OnRemoteVolumesChanged(string deviceId, IReadOnlyList<RemoteVolume> volumes)
    {
        // The event is a wake-up hint. Reconciliation still performs an authoritative fs.volumes
        // request over the bulk lane so reconnects and dropped events converge on the same state.
        Queue(deviceId);
    }

    private void OnConfigurationChanged()
    {
        foreach (var snapshot in _sessions.GetSnapshots()) Queue(snapshot.DeviceId);
        foreach (var key in _mounted.Keys) Queue(key.DeviceId);
    }

    private void Queue(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || _shutdown.IsCancellationRequested) return;
        if (_pending.TryAdd(deviceId, 0)) _queue.Writer.TryWrite(deviceId);
    }

    private async Task RunLoopAsync(CancellationToken outerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerToken, _shutdown.Token);
        try
        {
            await foreach (var deviceId in _queue.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                _pending.TryRemove(deviceId, out _);
                try { await ReconcileAsync(deviceId, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
                catch (Exception ex) { _log?.Invoke($"Remote mount reconciliation for {deviceId} failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            await UnmountAllAsync().ConfigureAwait(false);
        }
    }

    private async Task ReconcileAsync(string deviceId, CancellationToken token)
    {
        var session = _sessions.GetSession(deviceId);
        if (session is null || !session.Online || !session.BulkReady)
        {
            await UnmountDeviceAsync(deviceId).ConfigureAwait(false);
            return;
        }

        var snapshot = session.Snapshot;
        if (snapshot.Device?.Capabilities.Any(c => c.Capability == Capability.FileSystem && c.Version > 0) != true)
            return;

        RemoteVolume[] volumes;
        try
        {
            volumes = await _volumeProvider(session, token).ConfigureAwait(false);
        }
        catch (RemoteProtocolException ex) when (ex.Message.Contains("not granted", StringComparison.OrdinalIgnoreCase) ||
                                                  ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke($"Remote filesystem access to {snapshot.Name} is not granted; removing its mounted drives.");
            foreach (var key in _manualMounts.Keys.Where(k => k.DeviceId == deviceId).ToArray())
                _manualMounts.TryRemove(key, out _);
            await UnmountDeviceAsync(deviceId).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or RemoteProtocolException)
        {
            // Keep existing mounts through a transient request failure. A real lane disconnect generates a
            // session change and takes the explicit unmount path above.
            _log?.Invoke($"Could not refresh remote volumes from {snapshot.Name}: {ex.Message}");
            return;
        }

        var available = volumes.ToDictionary(v => v.Id, StringComparer.Ordinal);

        foreach (var suppressed in _suppressed.Keys.Where(k => k.DeviceId == deviceId).ToArray())
            if (!available.ContainsKey(suppressed.VolumeId)) _suppressed.TryRemove(suppressed, out _);

        foreach (var existing in _mounted.Keys.Where(k => k.DeviceId == deviceId).ToArray())
        {
            if (!available.ContainsKey(existing.VolumeId) ||
                (!_manualMounts.ContainsKey(existing) && !ShouldAutoMount(available[existing.VolumeId])))
            {
                await UnmountAsync(existing).ConfigureAwait(false);
                if (!available.ContainsKey(existing.VolumeId)) _manualMounts.TryRemove(existing, out _);
            }
        }

        // Explicit mounts survive temporary lane loss. Restore them even when automatic
        // removable mounting is disabled, provided the remote volume is still available.
        foreach (var key in _manualMounts.Keys.Where(k => k.DeviceId == deviceId).ToArray())
        {
            if (!available.TryGetValue(key.VolumeId, out var volume))
            {
                _manualMounts.TryRemove(key, out _);
                continue;
            }
            if (!_mounted.ContainsKey(key))
                await MountAsync(key, snapshot.Name, session, volume, token).ConfigureAwait(false);
        }

        foreach (var volume in available.Values.Where(ShouldAutoMount))
        {
            var key = new MountKey(deviceId, volume.Id);
            if (_mounted.ContainsKey(key) || _suppressed.ContainsKey(key)) continue;
            await MountAsync(key, snapshot.Name, session, volume, token).ConfigureAwait(false);
        }
    }

    private bool ShouldAutoMount(RemoteVolume volume) =>
        string.Equals(volume.Kind, "removable", StringComparison.OrdinalIgnoreCase)
            ? _configuration.AutoMountRemoteRemovable
            : string.Equals(volume.Kind, "fixed", StringComparison.OrdinalIgnoreCase) && _configuration.AutoMountRemoteMainDrive;

    private async Task MountAsync(MountKey key, string deviceName, PeerSession session, RemoteVolume volume,
        CancellationToken token)
    {
        var adapter = _adapterFactory();
        if (!adapter.IsAvailable)
        {
            await adapter.DisposeAsync().ConfigureAwait(false);
            _log?.Invoke($"Cannot mount {volume.Name} from {deviceName}: {adapter.PlatformName} is unavailable.");
            return;
        }

        var label = volume with { Name = $"{volume.Name} ({deviceName})" };
        try
        {
            var mountPoint = _mountPointFactory(key.DeviceId, deviceName, volume);
            await adapter.MountAsync(mountPoint, label,
                RemoteFileSystemOperationsClient.ForPeer(session, volume.Id), token).ConfigureAwait(false);
            var mounted = new MountedVolume(key, deviceName, volume, adapter);
            if (!_mounted.TryAdd(key, mounted))
            {
                await adapter.UnmountAsync(CancellationToken.None).ConfigureAwait(false);
                await adapter.DisposeAsync().ConfigureAwait(false);
                return;
            }
            _log?.Invoke($"Mounted {volume.Name} from {deviceName} at {adapter.MountedAt ?? "a WinFsp drive"}.");
        }
        catch
        {
            await adapter.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task UnmountDeviceAsync(string deviceId)
    {
        foreach (var key in _mounted.Keys.Where(k => k.DeviceId == deviceId).ToArray())
            await UnmountAsync(key).ConfigureAwait(false);
    }

    private async Task UnmountAsync(MountKey key)
    {
        if (!_mounted.TryRemove(key, out var mounted)) return;
        try { await mounted.Adapter.UnmountAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log?.Invoke($"Unmount of {mounted.Volume.Name} from {mounted.DeviceName} failed: {ex.Message}"); }
        finally { await mounted.Adapter.DisposeAsync().ConfigureAwait(false); }
        _log?.Invoke($"Unmounted {mounted.Volume.Name} from {mounted.DeviceName}.");
    }

    private async Task UnmountAllAsync()
    {
        foreach (var key in _mounted.Keys.ToArray()) await UnmountAsync(key).ConfigureAwait(false);
    }

    private ConfiguredPeer ResolvePeer(string? deviceId) => _configuration.Resolve(deviceId)
        ?? throw new InvalidOperationException(deviceId is null
            ? "No default remote device is configured."
            : $"No configured peer matches '{deviceId}'.");

    private PeerSession RequireReadySession(ConfiguredPeer peer)
    {
        var session = _sessions.GetSession(peer.DeviceId)
            ?? throw new IOException($"Device {peer.Name} is offline.");
        if (!session.Online || !session.BulkReady) throw new IOException($"Filesystem transport to {peer.Name} is offline.");
        if (session.Snapshot.Device?.Capabilities.Any(c => c.Capability == Capability.FileSystem && c.Version > 0) != true)
            throw new NotSupportedException($"Device {peer.Name} does not advertise remote filesystem support.");
        return session;
    }

    private static RemoteVolume ResolveVolume(IEnumerable<RemoteVolume> volumes, string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var all = volumes.ToArray();
        var exact = all.Where(v => string.Equals(v.Id, query, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(v.Name, query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) throw new InvalidOperationException($"Remote volume '{query}' is ambiguous.");
        var prefix = all.Where(v => v.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return prefix.Length switch
        {
            1 => prefix[0],
            > 1 => throw new InvalidOperationException($"Remote volume '{query}' is ambiguous."),
            _ => throw new InvalidOperationException($"Remote volume '{query}' was not found.")
        };
    }

    private static bool MatchesVolume(RemoteVolume volume, string query) =>
        string.Equals(volume.Id, query, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(volume.Name, query, StringComparison.OrdinalIgnoreCase) ||
        volume.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase);

    private static RemoteMountSnapshot ToSnapshot(MountedVolume item) =>
        new(item.Key.DeviceId, item.DeviceName, item.Key.VolumeId, item.Volume.Name, item.Volume.Kind,
            item.Adapter.MountedAt, item.Volume.ReadOnly, item.Volume.TotalBytes, item.Volume.FreeBytes,
            item.Volume.FileSystem);

    private static IRemoteFileSystemMountAdapter CreatePlatformAdapter()
    {
#if XAS_WINDOWS_TARGET
        return OperatingSystem.IsWindows()
            ? new WinFspRemoteFileSystemMountAdapter()
            : new UnavailableRemoteFileSystemMountAdapter("WinFsp");
#elif XAS_LINUX_TARGET
        return OperatingSystem.IsLinux()
            ? new LinuxFuseRemoteFileSystemMountAdapter()
            : new UnavailableRemoteFileSystemMountAdapter("FUSE");
#else
        return OperatingSystem.IsWindows()
            ? new WinFspRemoteFileSystemMountAdapter()
            : OperatingSystem.IsLinux()
                ? new LinuxFuseRemoteFileSystemMountAdapter()
                : new UnavailableRemoteFileSystemMountAdapter("native");
#endif
    }

    private static bool DefaultAvailabilityProbe()
    {
        var adapter = CreatePlatformAdapter();
        try { return adapter.IsAvailable; }
        finally { adapter.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private string DefaultMountPoint(string deviceId, string deviceName, RemoteVolume volume)
    {
        if (OperatingSystem.IsWindows()) return string.Empty;
        if (!OperatingSystem.IsLinux()) return string.Empty;
        return _mountPaths.GetMountPoint(deviceId, deviceName, volume);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessions.SessionChanged -= OnSessionChanged;
        _sessions.SessionRemoved -= OnSessionRemoved;
        _sessions.RemoteVolumesChanged -= OnRemoteVolumesChanged;
        _configuration.Changed -= OnConfigurationChanged;
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        else await UnmountAllAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private readonly record struct MountKey(string DeviceId, string VolumeId);
    private sealed record MountedVolume(MountKey Key, string DeviceName, RemoteVolume Volume,
        IRemoteFileSystemMountAdapter Adapter);
}
