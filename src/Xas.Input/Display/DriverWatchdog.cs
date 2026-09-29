namespace Xas.Input.Display;

/// <summary>Runs a driver keepalive immediately, then at a fixed interval until stopped.</summary>
internal sealed class DriverWatchdog(Func<bool> ping, TimeSpan interval) : IAsyncDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null) return;

            // Ping inline: callers can guarantee the initial keepalive happens before issuing Add.
            ping();
            _stop = new CancellationTokenSource();
            _loop = RunAsync(_stop.Token);
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            loop = _loop;
            stop = _stop;
            stop?.Cancel();
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_loop, loop)) return;
            _loop = null;
            _stop = null;
            stop?.Dispose();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task RunAsync(CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(interval, token).ConfigureAwait(false);
            ping();
        }
    }
}
