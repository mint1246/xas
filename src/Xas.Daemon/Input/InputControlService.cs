using Xas.Core;
using Xas.Core.Security;
using System.Diagnostics;

namespace Xas.Daemon.Input;

/// <summary>Owns the single input lease available to authenticated peers.</summary>
public sealed class InputControlService : IAsyncDisposable
{
    private readonly PeerPermissionStore _permissions;
    private readonly IInputInjectionBackend _backend;
    private readonly IInputPipelineMetrics? _metrics;
    private readonly object _gate = new();
    private ConnectionInputSession? _lease;
    private uint _nextId;
    private bool _releasing;
    private bool _disposed;

    public InputControlService(PeerPermissionStore permissions, IInputInjectionBackend backend,
        IInputPipelineMetrics? metrics = null)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _metrics = metrics;
    }

    public bool IsAvailable => !_disposed && _backend.IsAvailable;
    public ushort ProtocolVersion => IsAvailable ? (ushort)(_backend is IAbsoluteInputInjectionBackend ? 2 : 1) : (ushort)0;

    public ConnectionInputSession CreateSession(string peerId)
    {
        ArgumentNullException.ThrowIfNull(peerId);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); }
        return new ConnectionInputSession(this, peerId);
    }

    internal uint Acquire(ConnectionInputSession session)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsAvailable) throw new PlatformNotSupportedException("A real input-injection backend is unavailable.");
            if (_lease is not null || _releasing) throw new InvalidOperationException("Another peer currently holds the input lease or its release is still in progress.");
            if (_nextId == uint.MaxValue) throw new InvalidOperationException("No input session IDs remain for this service lifetime.");
            var id = ++_nextId;
            _lease = session;
            return id;
        }
    }

    internal bool IsCurrent(ConnectionInputSession session)
    {
        lock (_gate) return !_disposed && ReferenceEquals(_lease, session);
    }

    internal async ValueTask ReleaseAsync(ConnectionInputSession session)
    {
        bool held;
        lock (_gate)
        {
            held = ReferenceEquals(_lease, session);
            if (held) { _lease = null; _releasing = true; }
        }
        if (!held) return;
        try { await _backend.ReleaseAllAsync(CancellationToken.None).ConfigureAwait(false); }
        finally { lock (_gate) _releasing = false; }
    }

    internal PeerPermissionStore Permissions => _permissions;
    internal IInputInjectionBackend Backend => _backend;

    internal void RecordMetric(InputPipelineStage stage, int eventCount)
    {
        if (_metrics is not { } metrics) return;
        var sample = new InputPipelineMetric(stage, Stopwatch.GetTimestamp(), eventCount);
        metrics.Record(in sample);
    }

    public async ValueTask DisposeAsync()
    {
        ConnectionInputSession? lease;
        lock (_gate) { if (_disposed) return; _disposed = true; lease = _lease; }
        if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        if (_backend is IAsyncDisposable asyncBackend) await asyncBackend.DisposeAsync().ConfigureAwait(false);
        else if (_backend is IDisposable backend) backend.Dispose();
    }
}
