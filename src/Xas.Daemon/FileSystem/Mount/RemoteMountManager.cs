using System.Collections.Concurrent;
using System.Threading.Channels;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.FileSystem;
using Xas.Core.Protocol;
using Xas.Daemon.Sessions;

namespace Xas.Daemon.FileSystem.Mount;

public sealed record RemoteMountSnapshot(string DeviceId, string DeviceName, string VolumeId, string VolumeName,
    string? MountPoint, bool ReadOnly, long? TotalBytes, long? FreeBytes, string? FileSystem);

/// <summary>
/// Reconciles remote removable volumes with native local mounts. The daemon owns this for its entire
/// lifetime so a CLI process is never responsible for keeping a drive letter alive.
/// </summary>
public sealed class RemoteMountManager : IAsyncDisposable
{
    private readonly LocalConfiguration _configuration;
    private readonly PeerSessionManager _sessions;
    private readonly Func<IRemoteFileSystemMountAdapter> _adapterFactory;
    private readonly Func<PeerSession, CancellationToken, ValueTask<RemoteVolume[]>> _volumeProvider;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<MountKey, MountedVolume> _mounted = new();
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
        Action<string>? log = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _adapterFactory = adapterFactory ?? (() => OperatingSystem.IsWindows()
            ? new WinFspRemoteFileSystemMountAdapter()
            : new UnavailableRemoteFileSystemMountAdapter("native"));
        _volumeProvider = volumeProvider ?? RemoteFileSystemOperationsClient.GetVolumesAsync;
        _log = log;
    }

    public IReadOnlyList<RemoteMountSnapshot> GetSnapshots() => _mounted.Values
        .Select(item => new RemoteMountSnapshot(item.Key.DeviceId, item.DeviceName, item.Key.VolumeId,
            item.Volume.Name, item.Adapter.MountedAt, item.Volume.ReadOnly, item.Volume.TotalBytes,
            item.Volume.FreeBytes, item.Volume.FileSystem))
        .OrderBy(item => item.DeviceName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.VolumeName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

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
        if (!_configuration.AutoMountRemoteRemovable || session is null || !session.Online || !session.BulkReady)
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

        var desired = volumes.Where(v => string.Equals(v.Kind, "removable", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(v => v.Id, StringComparer.Ordinal);

        foreach (var existing in _mounted.Keys.Where(k => k.DeviceId == deviceId).ToArray())
        {
            if (!desired.ContainsKey(existing.VolumeId)) await UnmountAsync(existing).ConfigureAwait(false);
        }

        foreach (var volume in desired.Values)
        {
            var key = new MountKey(deviceId, volume.Id);
            if (_mounted.ContainsKey(key)) continue;
            await MountAsync(key, snapshot.Name, session, volume, token).ConfigureAwait(false);
        }
    }

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
            await adapter.MountAsync(string.Empty, label,
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
