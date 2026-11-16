using System.Collections.Concurrent;

namespace Xas.Core.Protocol;

/// <summary>Correlates concurrent requests and dispatches incoming requests over one frame connection.</summary>
public sealed class MultiplexedProtocolPeer : IAsyncDisposable
{
    private readonly IFrameConnection _connection;
    private readonly Func<ProtocolMessage, CancellationToken, ValueTask<ProtocolMessage>> _requestHandler;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<ProtocolMessage>> _pending = new();
    private readonly ConcurrentDictionary<uint, CancellationTokenSource> _incoming = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task _reader = Task.CompletedTask;
    private int _started;
    private uint _nextId;
    private int _disposed;
    private Exception? _terminalException;

    public MultiplexedProtocolPeer(
        IFrameConnection connection,
        Func<ProtocolMessage, CancellationToken, ValueTask<ProtocolMessage>> requestHandler,
        bool startImmediately = true)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        if (startImmediately) Start();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _reader = ReadLoopAsync();
    }

    /// <summary>Raised for messages not consumed by request/response handling.</summary>
    public event Func<ProtocolMessage, ValueTask>? MessageReceived;

    /// <summary>Completes when the connection closes or the receive loop stops.</summary>
    public Task Completion => _reader;

    /// <summary>The transport or protocol failure that ended the receive loop, if any.</summary>
    public Exception? TerminalException => Volatile.Read(ref _terminalException);

    public ValueTask SendAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) == 0)
            throw new InvalidOperationException("Protocol peer has not been started.");
        return _connection.SendAsync(message, cancellationToken);
    }

    public async ValueTask<ProtocolMessage> RequestAsync(
        string method,
        byte[] payload,
        uint streamId = 0, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) == 0)
            throw new InvalidOperationException("Protocol peer has not been started.");
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var id = NextRequestId();
        var completion = new TaskCompletionSource<ProtocolMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("Request id collision.");
        using var registration = cancellationToken.Register(static state =>
        {
            var tuple = ((
                MultiplexedProtocolPeer Peer,
                uint Id,
                TaskCompletionSource<ProtocolMessage> Completion))state!;
            if (tuple.Peer._pending.TryRemove(tuple.Id, out _)) tuple.Completion.TrySetCanceled();
        }, (this, id, completion));
        try
        {
            var request = new ProtocolMessage(
                MessageKind.Request,
                id,
                streamId,
                method,
                payload);
            await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            if (cancellationToken.IsCancellationRequested)
                await SendCancellationAsync(id, streamId, method).ConfigureAwait(false);
            throw;
        }
    }

    private async Task SendCancellationAsync(uint requestId, uint streamId, string method)
    {
        try
        {
            var cancel = new ProtocolMessage(
                MessageKind.Cancel,
                requestId,
                streamId,
                method,
                Array.Empty<byte>());
            await _connection.SendAsync(cancel, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The transport may already be closed.
        }
    }

    private uint NextRequestId()
    {
        while (true)
        {
            var id = unchecked(Interlocked.Increment(ref _nextId));
            if (id != 0 && !_pending.ContainsKey(id)) return id;
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var message = await _connection.ReceiveAsync(_shutdown.Token).ConfigureAwait(false);
                if (message is null) throw new EndOfStreamException("Protocol connection closed.");
                await DispatchMessageAsync(message).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            Volatile.Write(ref _terminalException, failure);
            var terminal = failure ?? new ObjectDisposedException(nameof(MultiplexedProtocolPeer));
            foreach (var item in _pending.Values) item.TrySetException(terminal);
            _pending.Clear();
            foreach (var cts in _incoming.Values) CancelQuietly(cts);
        }
    }

    private async ValueTask DispatchMessageAsync(ProtocolMessage message)
    {
        switch (message.Kind)
        {
            case MessageKind.Response:
            case MessageKind.Error:
                CompletePendingRequest(message);
                return;
            case MessageKind.Request:
                await StartIncomingRequestAsync(message).ConfigureAwait(false);
                return;
            case MessageKind.Cancel:
                CancelIncomingRequest(message.RequestId);
                return;
            default:
                await PublishMessageAsync(message).ConfigureAwait(false);
                return;
        }
    }

    private void CompletePendingRequest(ProtocolMessage message)
    {
        if (message.RequestId == 0
            || !_pending.TryRemove(message.RequestId, out var pending))
            return;

        if (message.Kind == MessageKind.Error)
        {
            pending.TrySetException(
                RemoteProtocolException.Decode(message.Method, message.Payload));
            return;
        }

        pending.TrySetResult(message);
    }

    private async Task StartIncomingRequestAsync(ProtocolMessage message)
    {
        if (message.RequestId == 0)
            throw new InvalidDataException("Request must have a nonzero request id.");

        var requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (_incoming.TryAdd(message.RequestId, requestCancellation))
        {
            _ = HandleRequestAsync(message, requestCancellation);
            return;
        }

        requestCancellation.Dispose();
        await SendErrorAsync(
            message,
            new InvalidOperationException("Duplicate request id."))
            .ConfigureAwait(false);
    }

    private void CancelIncomingRequest(uint requestId)
    {
        if (_incoming.TryGetValue(requestId, out var requestCancellation))
            CancelQuietly(requestCancellation);
    }

    private async ValueTask PublishMessageAsync(ProtocolMessage message)
    {
        var handler = MessageReceived;
        if (handler is null)
            return;

        foreach (Func<ProtocolMessage, ValueTask> subscriber in handler.GetInvocationList())
            await subscriber(message).ConfigureAwait(false);
    }

    private async Task HandleRequestAsync(ProtocolMessage request, CancellationTokenSource requestCts)
    {
        try
        {
            var response = await _requestHandler(request, requestCts.Token).ConfigureAwait(false);
            if (response.Kind is not (MessageKind.Response or MessageKind.Error))
                throw new InvalidOperationException(
                    "Request handlers must return a Response or Error message.");
            response = response with { RequestId = request.RequestId, StreamId = request.StreamId };
            await _connection.SendAsync(response, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            try { await SendErrorAsync(request, ex).ConfigureAwait(false); }
            catch (Exception) when (_shutdown.IsCancellationRequested) { }
        }
        finally
        {
            _incoming.TryRemove(request.RequestId, out _);
            requestCts.Dispose();
        }
    }

    private ValueTask SendErrorAsync(ProtocolMessage request, Exception error)
    {
        var payload = request.Method.StartsWith("fs.", StringComparison.Ordinal)
            ? RemoteProtocolException.Encode(error)
            : System.Text.Encoding.UTF8.GetBytes(error.Message);
        var errorMessage = new ProtocolMessage(
            MessageKind.Error,
            request.RequestId,
            request.StreamId,
            request.Method,
            payload);
        return _connection.SendAsync(errorMessage, _shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        foreach (var cts in _incoming.Values) CancelQuietly(cts);
        try { await _reader.ConfigureAwait(false); }
        catch (Exception) { /* ReadLoopAsync reports failures through outstanding requests. */ }
        await _connection.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private static void CancelQuietly(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

public sealed class RemoteProtocolException : Exception
{
    private const string EnvelopePrefix = "XAS-ERROR-1\\n";

    public RemoteProtocolException(string method, byte[] payload)
        : this(method, System.Text.Encoding.UTF8.GetString(payload), "unknown") { }

    private RemoteProtocolException(string method, string message, string code)
        : base($"Remote protocol error for '{method}': {message}")
    {
        Code = code;
        RemoteMessage = message;
    }

    public string Code { get; }
    public string RemoteMessage { get; }

    internal static byte[] Encode(Exception exception)
    {
        var code = exception switch
        {
            DirectoryNotFoundException => "directory-not-found",
            FileNotFoundException => "file-not-found",
            UnauthorizedAccessException => "access-denied",
            InvalidDataException or ArgumentException => "invalid-argument",
            IOException => "io-error",
            NotSupportedException => "not-supported",
            _ => "unknown"
        };
        var errorEnvelope = new ErrorEnvelope(code, exception.Message);
        var envelope = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(errorEnvelope);
        return System.Text.Encoding.UTF8.GetBytes(EnvelopePrefix).Concat(envelope).ToArray();
    }

    internal static RemoteProtocolException Decode(string method, byte[] payload)
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes(EnvelopePrefix);
        if (!payload.AsSpan().StartsWith(prefix)) return new RemoteProtocolException(method, payload);
        try
        {
            var envelope = System.Text.Json.JsonSerializer.Deserialize<ErrorEnvelope>(
                payload.AsSpan(prefix.Length));
            if (envelope is { Code: not null, Message: not null })
                return new RemoteProtocolException(method, envelope.Message, envelope.Code);
        }
        catch (System.Text.Json.JsonException) { }
        return new RemoteProtocolException(method, payload);
    }

    private sealed record ErrorEnvelope(string Code, string Message);
}
