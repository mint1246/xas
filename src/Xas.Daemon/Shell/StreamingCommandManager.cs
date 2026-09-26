using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text;
using Xas.Core;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Services;

namespace Xas.Daemon.Shell;

/// <summary>Runs remote one-shot commands while forwarding all three standard streams live.</summary>
public sealed class StreamingCommandManager : IAsyncDisposable
{
    private readonly string _peerId;
    private readonly PeerPermissionStore _permissions;
    private readonly Func<ProtocolMessage, CancellationToken, ValueTask> _send;
    private readonly IShellBackend _backend;
    private readonly ConcurrentDictionary<uint, CommandSession> _sessions = new();
    private int _nextId;
    private int _disposed;

    public StreamingCommandManager(string peerId, PeerPermissionStore permissions,
        Func<ProtocolMessage, CancellationToken, ValueTask> send, IShellBackend? backend = null)
    {
        _peerId = peerId ?? throw new ArgumentNullException(nameof(peerId));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _backend = backend ?? new ProcessShellBackend();
    }

    public async ValueTask<ProtocolMessage> HandleRequestAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (request.Method == ShellExecWire.Open)
            return await OpenAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Method == ShellExecWire.Close)
        {
            var id = ShellExecWire.DecodeSessionId(request.Payload);
            if (_sessions.TryRemove(id, out var session))
            {
                session.Input.Complete();
                session.Cancellation.Cancel();
                await session.Completion.ConfigureAwait(false);
                session.Dispose();
            }
            return Reply(request, []);
        }
        throw new NotSupportedException($"Unknown streaming shell request: {request.Method}");
    }

    public async ValueTask HandleMessageAsync(ProtocolMessage message)
    {
        if (message.Method != ShellExecWire.Stdin || message.Kind is not (MessageKind.StreamData or MessageKind.StreamEnd))
            throw new InvalidDataException($"Unexpected streaming shell message: {message.Method}");
        if (!_sessions.TryGetValue(message.StreamId, out var session)) return;
        if (message.Kind == MessageKind.StreamEnd)
        {
            if (message.Payload.Length != 0) throw new InvalidDataException("Shell stdin EOF cannot contain data.");
            session.Input.Complete();
            return;
        }
        if (message.Payload.Length > ShellExecWire.MaxChunkBytes)
            throw new InvalidDataException("Shell stdin chunk exceeds the supported size.");
        if (message.Payload.Length == 0)
            throw new InvalidDataException("Shell stdin data chunks cannot be empty.");
        await session.Input.WriteAsync(message.Payload, session.Cancellation.Token).ConfigureAwait(false);
    }

    private async ValueTask<ProtocolMessage> OpenAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        if (!_permissions.IsAllowed(_peerId, Capability.Shell))
            throw new UnauthorizedAccessException("Shell access is not granted on this device for this peer.");
        var shellRequest = ShellWire.DecodeRequest(request.Payload);
        if (shellRequest.Elevated && OperatingSystem.IsWindows() && !_permissions.IsAllowed(_peerId, Capability.PrivilegedShell))
            throw new UnauthorizedAccessException("Privileged shell access is not granted on this device for this peer.");
        if (shellRequest.Mode == ShellMode.Interactive)
            throw new NotSupportedException("Interactive shells use the PTY/ConPTY protocol.");

        cancellationToken.ThrowIfCancellationRequested();
        CommandSession session;
        while (true)
        {
            var id = unchecked((uint)Interlocked.Increment(ref _nextId));
            if (id == 0) continue;
            session = new CommandSession(id);
            if (_sessions.TryAdd(id, session)) break;
            session.Dispose();
        }

        session.Completion = RunAsync(session, shellRequest);
        return Reply(request, ShellExecWire.EncodeSessionId(session.Id));
    }

    private async Task RunAsync(CommandSession session, ShellRequest request)
    {
        try
        {
            var exitCode = await _backend.RunAsync(request, session.Input, new OutputStream(_send, session.Id, ShellExecWire.Stdout),
                new OutputStream(_send, session.Id, ShellExecWire.Stderr), session.Cancellation.Token).ConfigureAwait(false);
            await SendAsync(MessageKind.Event, session.Id, ShellExecWire.Exit, ShellExecWire.EncodeExitCode(exitCode), session.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            try
            {
                await SendAsync(MessageKind.Event, session.Id, ShellExecWire.Error,
                    Encoding.UTF8.GetBytes(ex.Message), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { /* The transport may have closed. */ }
        }
    }

    private ValueTask SendAsync(MessageKind kind, uint id, string method, byte[] payload, CancellationToken ct) =>
        _send(new ProtocolMessage(kind, 0, id, method, payload), ct);

    private static ProtocolMessage Reply(ProtocolMessage request, byte[] payload) =>
        new(MessageKind.Response, request.RequestId, request.StreamId, request.Method, payload);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var pair in _sessions.ToArray())
        {
            if (!_sessions.TryRemove(pair.Key, out var session)) continue;
            session.Input.Complete();
            session.Cancellation.Cancel();
            try { await session.Completion.ConfigureAwait(false); }
            catch (Exception) { }
            session.Dispose();
        }
    }

    private sealed class CommandSession(uint id) : IDisposable
    {
        public uint Id { get; } = id;
        public ChannelInputStream Input { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public void Dispose() { Input.Dispose(); Cancellation.Dispose(); }
    }

    /// <summary>A bounded queue applies backpressure if a sender outruns the child process.</summary>
    private sealed class ChannelInputStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        private byte[]? _current;
        private int _offset;
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _channel.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        public async ValueTask WriteAsync(byte[] bytes, CancellationToken ct) => await _channel.Writer.WriteAsync(bytes, ct).ConfigureAwait(false);
        public void Complete() => _channel.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            while (_current is null || _offset == _current.Length)
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                if (_channel.Reader.TryRead(out var next)) { _current = next; _offset = 0; }
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed) { _disposed = true; Complete(); }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() { Dispose(true); GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class OutputStream(Func<ProtocolMessage, CancellationToken, ValueTask> send, uint id, string method) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(buffer.Length, ShellExecWire.MaxChunkBytes);
                await send(new ProtocolMessage(MessageKind.StreamData, 0, id, method, buffer[..count].ToArray()), cancellationToken)
                    .ConfigureAwait(false);
                buffer = buffer[count..];
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
