using Xas.Core.FileSystem;

namespace Xas.Daemon.FileSystem;

/// <summary>
/// Bounded snapshot watcher for OS-mounted removable volumes. It observes DriveInfo snapshots; it does
/// not mount media or guarantee classification of devices the OS reports as fixed disks.
/// </summary>
public sealed class RemovableVolumeWatcher(Func<RemoteVolume[]> getVolumes, TimeSpan? pollInterval = null)
    : IAsyncDisposable
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private Task? _loop;

    public event Action<IReadOnlyList<RemoteVolume>>? Changed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
        if (_pollInterval < TimeSpan.FromMilliseconds(250)) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        lock (_gate)
        {
            if (_loop is not null) throw new InvalidOperationException("Volume watcher is already running.");
            _loop = WatchAsync(_shutdown.Token);
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        var previous = GetRemovable();
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var current = GetRemovable();
            if (Same(previous, current)) continue;
            previous = current;
            var handlers = Changed;
            if (handlers is null) continue;
            foreach (Action<IReadOnlyList<RemoteVolume>> handler in handlers.GetInvocationList())
            {
                try { handler(current); }
                catch { /* A subscriber must not stop volume discovery for other subscribers. */ }
            }
        }
    }

    private RemoteVolume[] GetRemovable() => getVolumes().Where(v => v.Kind == "removable")
        .OrderBy(v => v.Id, StringComparer.Ordinal).ToArray();

    private static bool Same(RemoteVolume[] left, RemoteVolume[] right) => left.SequenceEqual(right);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _shutdown.Dispose();
    }
}
