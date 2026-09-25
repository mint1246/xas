using System.Buffers.Binary;
using Xas.Core;
using Xas.Core.Security;

namespace Xas.Daemon.Input;

/// <summary>Input RPC and event handling for one authenticated protocol connection.</summary>
public sealed class ConnectionInputSession : IAsyncDisposable
{
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(4);
    private readonly InputControlService _owner;
    private readonly string _peerId;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly HashSet<ushort> _keys = [];
    private readonly HashSet<ushort> _buttons = [];
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _watchdog;
    private uint _id;
    private DateTime _lastActivity;
    private bool _closed;
    private int _disposeStarted;

    internal ConnectionInputSession(InputControlService owner, string peerId)
    { _owner = owner; _peerId = peerId; }

    public async ValueTask<ProtocolMessage> HandleRequestAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (request.Kind != MessageKind.Request) throw new InvalidDataException("Expected an input RPC request.");
            switch (request.Method)
            {
                case "input.open":
                    if (request.Payload.Length != 0 || _id != 0) throw new InvalidDataException("input.open requires an empty payload and no active session.");
                    if (!_owner.Permissions.IsAllowed(_peerId, Capability.Input)) throw new UnauthorizedAccessException("Input access is not granted for this peer.");
                    _id = _owner.Acquire(this);
                    _lastActivity = DateTime.UtcNow;
                    _watchdog = WatchdogAsync(_shutdown.Token);
                    var response = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(response, _id);
                    return new ProtocolMessage(MessageKind.Response, request.RequestId, 0, request.Method, response);
                case "input.close":
                    if (request.Payload.Length != 4 || _id == 0 || BinaryPrimitives.ReadUInt32BigEndian(request.Payload) != _id)
                        throw new InvalidDataException("input.close requires this connection's active four-byte session ID.");
                    await CloseCoreAsync().ConfigureAwait(false);
                    return new ProtocolMessage(MessageKind.Response, request.RequestId, 0, request.Method, []);
                default: throw new NotSupportedException($"Unknown input request: {request.Method}");
            }
        }
        finally { _serial.Release(); }
    }

    public async ValueTask HandleMessageAsync(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed) throw new ObjectDisposedException(nameof(ConnectionInputSession));
            if (message.RequestId != 0 || message.StreamId == 0 || message.Method != "input.event" || _id == 0 || message.StreamId != _id)
            { await FailClosedAsync().ConfigureAwait(false); throw new InvalidDataException("Invalid input event stream/session."); }
            if (message.Kind == MessageKind.StreamEnd)
            {
                if (message.Payload.Length != 0) { await FailClosedAsync().ConfigureAwait(false); throw new InvalidDataException("input.event StreamEnd payload must be empty."); }
                await CloseCoreAsync().ConfigureAwait(false);
                return;
            }
            if (message.Kind != MessageKind.StreamData || message.Payload.Length > 12_288)
            { await FailClosedAsync().ConfigureAwait(false); throw new InvalidDataException("Expected input.event data of at most 12288 bytes or an empty StreamEnd."); }
            InputEvent[] events;
            try { events = InputWire.Decode(message.Payload); }
            catch { await FailClosedAsync().ConfigureAwait(false); throw; }
            _lastActivity = DateTime.UtcNow;
            try
            {
                foreach (var e in events)
                {
                    Track(e);
                    if (e.Kind != InputEventKind.KeepAlive)
                        await _owner.Backend.InjectAsync(e, _shutdown.Token).ConfigureAwait(false);
                }
            }
            catch { await FailClosedAsync().ConfigureAwait(false); throw; }
        }
        finally { _serial.Release(); }
    }

    private void Track(InputEvent e)
    {
        if (e.Kind == InputEventKind.Key) { if (e.Down) _keys.Add(e.Code); else _keys.Remove(e.Code); }
        if (e.Kind == InputEventKind.Button) { if (e.Down) _buttons.Add(e.Code); else _buttons.Remove(e.Code); }
    }

    private async Task WatchdogAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await _serial.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!_closed && DateTime.UtcNow - _lastActivity >= InactivityTimeout)
                    { await FailClosedAsync().ConfigureAwait(false); return; }
                }
                finally { _serial.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async ValueTask CloseCoreAsync()
    {
        if (_closed) return;
        _closed = true; _shutdown.Cancel(); _id = 0;
        _keys.Clear(); _buttons.Clear();
        await _owner.ReleaseAsync(this).ConfigureAwait(false);
    }

    private async ValueTask FailClosedAsync()
    {
        try { await CloseCoreAsync().ConfigureAwait(false); }
        catch { /* Preserve the protocol/backend failure; the lease is already invalidated. */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _shutdown.Cancel();
        if (_watchdog is not null) { try { await _watchdog.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        await _serial.WaitAsync().ConfigureAwait(false);
        try { await FailClosedAsync().ConfigureAwait(false); }
        finally { _serial.Release(); _shutdown.Dispose(); _serial.Dispose(); }
    }
}
