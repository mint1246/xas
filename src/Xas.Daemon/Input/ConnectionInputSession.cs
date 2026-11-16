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

    internal ConnectionInputSession(InputControlService service, string peerId)
    {
        _owner = service;
        _peerId = peerId;
    }

    public async ValueTask<ProtocolMessage> HandleRequestAsync(
        ProtocolMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (request.Kind != MessageKind.Request)
            {
                throw new InvalidDataException("Expected an input RPC request.");
            }

            return request.Method switch
            {
                "input.open" => await OpenAsync(request, cancellationToken).ConfigureAwait(false),
                "input.close" => await CloseAsync(request).ConfigureAwait(false),
                "input.release" => await ReleaseAsync(request, cancellationToken).ConfigureAwait(false),
                _ => throw new NotSupportedException($"Unknown input request: {request.Method}")
            };
        }
        finally
        {
            _serial.Release();
        }
    }

    private async ValueTask<ProtocolMessage> OpenAsync(
        ProtocolMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Payload.Length != 0 || _id != 0)
        {
            throw new InvalidDataException(
                "input.open requires an empty payload and no active session.");
        }
        if (!_owner.Permissions.IsAllowed(_peerId, Capability.Input))
        {
            throw new UnauthorizedAccessException("Input access is not granted for this peer.");
        }

        _id = _owner.Acquire(this);
        if (_owner.Backend is IInputActivationBackend activation)
        {
            try
            {
                await activation.ActivateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await CloseCoreAsync().ConfigureAwait(false);
                throw;
            }
        }

        _lastActivity = DateTime.UtcNow;
        _watchdog = WatchdogAsync(_shutdown.Token);
        var response = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(response, _id);
        return CreateReply(request, response);
    }

    private async ValueTask<ProtocolMessage> CloseAsync(ProtocolMessage request)
    {
        EnsureSessionIdMatches(request, "input.close");
        await CloseCoreAsync().ConfigureAwait(false);
        return CreateReply(request, []);
    }

    private async ValueTask<ProtocolMessage> ReleaseAsync(
        ProtocolMessage request,
        CancellationToken cancellationToken)
    {
        EnsureSessionIdMatches(request, "input.release");
        await _owner.Backend.ReleaseAllAsync(cancellationToken).ConfigureAwait(false);
        ClearPressedInputs();
        _lastActivity = DateTime.UtcNow;
        return CreateReply(request, []);
    }

    private void EnsureSessionIdMatches(ProtocolMessage request, string method)
    {
        if (request.Payload.Length != sizeof(uint)
            || _id == 0
            || BinaryPrimitives.ReadUInt32BigEndian(request.Payload) != _id)
        {
            throw new InvalidDataException(
                $"{method} requires this connection's active four-byte session ID.");
        }
    }

    private static ProtocolMessage CreateReply(ProtocolMessage request, byte[] payload) =>
        new(MessageKind.Response, request.RequestId, 0, request.Method, payload);

    public async ValueTask HandleMessageAsync(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            await ProcessInputMessageAsync(message).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task ProcessInputMessageAsync(ProtocolMessage message)
    {
        await ValidateInputMessageAsync(message).ConfigureAwait(false);
        if (message.Kind == MessageKind.StreamEnd)
        {
            await CloseCoreAsync().ConfigureAwait(false);
            return;
        }

        var events = await DecodeInputEventsAsync(message.Payload).ConfigureAwait(false);
        _owner.RecordMetric(InputPipelineStage.Receive, events.Length);
        _lastActivity = DateTime.UtcNow;
        try
        {
            TrackInputEvents(events);
            await InjectInputEventsAsync(events).ConfigureAwait(false);
            _owner.RecordMetric(InputPipelineStage.Inject, events.Length);
        }
        catch
        {
            await FailClosedAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ValidateInputMessageAsync(ProtocolMessage message)
    {
        if (message.RequestId != 0 || message.StreamId == 0 || message.Method != "input.event"
            || _id == 0 || message.StreamId != _id)
        {
            await RejectInputMessageAsync("Invalid input event stream/session.").ConfigureAwait(false);
        }
        if (message.Kind == MessageKind.StreamEnd && message.Payload.Length != 0)
        {
            await RejectInputMessageAsync("input.event StreamEnd payload must be empty.")
                .ConfigureAwait(false);
        }
        if (message.Kind is not (MessageKind.StreamEnd or MessageKind.StreamData)
            || message.Kind == MessageKind.StreamData && message.Payload.Length > 12_288)
        {
            await RejectInputMessageAsync(
                "Expected input.event data of at most 12288 bytes or an empty StreamEnd.")
                .ConfigureAwait(false);
        }
    }

    private async Task RejectInputMessageAsync(string message)
    {
        await FailClosedAsync().ConfigureAwait(false);
        throw new InvalidDataException(message);
    }

    private async Task<InputEvent[]> DecodeInputEventsAsync(byte[] payload)
    {
        try
        {
            return InputWire.Decode(payload);
        }
        catch
        {
            await FailClosedAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void TrackInputEvents(IEnumerable<InputEvent> events)
    {
        foreach (var inputEvent in events)
        {
            if (inputEvent.Kind == InputEventKind.MoveAbsolute && _owner.ProtocolVersion < 2)
            {
                throw new NotSupportedException(
                    "The active input backend cannot position the pointer absolutely.");
            }
            Track(inputEvent);
        }
    }

    private async Task InjectInputEventsAsync(InputEvent[] events)
    {
        if (_owner.Backend is IInputBatchInjectionBackend batchBackend)
        {
            // Batch injection preserves packet order and uses a single local pipe frame.
            await batchBackend.InjectBatchAsync(events, _shutdown.Token).ConfigureAwait(false);
            return;
        }

        foreach (var inputEvent in events)
        {
            if (inputEvent.Kind == InputEventKind.KeepAlive)
            {
                continue;
            }

            await _owner.Backend.InjectAsync(inputEvent, _shutdown.Token).ConfigureAwait(false);
        }
    }

    private void Track(InputEvent e)
    {
        if (e.Kind == InputEventKind.Key)
        {
            if (e.Down)
            {
                _keys.Add(e.Code);
            }
            else
            {
                _keys.Remove(e.Code);
            }
        }
        if (e.Kind == InputEventKind.Button)
        {
            if (e.Down)
            {
                _buttons.Add(e.Code);
            }
            else
            {
                _buttons.Remove(e.Code);
            }
        }
    }

    private void ClearPressedInputs()
    {
        _keys.Clear();
        _buttons.Clear();
    }

    private async Task WatchdogAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (await CloseIfInactiveAsync(token).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> CloseIfInactiveAsync(CancellationToken token)
    {
        await _serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_closed || DateTime.UtcNow - _lastActivity < InactivityTimeout)
            {
                return false;
            }

            await FailClosedAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _serial.Release();
        }
    }

    private async ValueTask CloseCoreAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _shutdown.Cancel();
        _id = 0;
        ClearPressedInputs();
        await _owner.ReleaseAsync(this).ConfigureAwait(false);
    }

    private async ValueTask FailClosedAsync()
    {
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
        }
        catch
        {
            // Preserve the protocol/backend failure; the lease is already invalidated.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        if (_watchdog is not null)
        {
            try
            {
                await _watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await FailClosedAsync().ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
            _shutdown.Dispose();
            _serial.Dispose();
        }
    }
}
