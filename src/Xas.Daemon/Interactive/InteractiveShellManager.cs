using System.Buffers.Binary;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Security;
using Xas.Daemon.Shell;
using Xas.Core.Privileged;

namespace Xas.Daemon.Interactive;

/// <summary>Manages interactive PTY sessions for one authenticated protocol connection.</summary>
public sealed class InteractiveShellManager : IAsyncDisposable
{
    private const int MaxSessions = 32;
    private const int MaxInputFrameBytes = 64 * 1024;
    private readonly string _peerId;
    private readonly PeerPermissionStore _permissions;
    private readonly Func<ProtocolMessage, CancellationToken, ValueTask> _send;
    private readonly IInteractiveShellBackend _backend;
    private readonly object _gate = new();
    private readonly Dictionary<uint, Session> _sessions = new();
    private ulong _nextId = 1;
    private int _opening;
    private bool _disposed;

    public InteractiveShellManager(string peerId, PeerPermissionStore permissions,
        Func<ProtocolMessage, CancellationToken, ValueTask> send)
        : this(peerId, permissions, send, OperatingSystem.IsWindows()
            ? new WindowsConPtyBackend()
            : new LinuxPtyBackend()) { }

    public InteractiveShellManager(string peerId, PeerPermissionStore permissions,
        Func<ProtocolMessage, CancellationToken, ValueTask> send, IInteractiveShellBackend backend)
    {
        _peerId = peerId ?? throw new ArgumentNullException(nameof(peerId));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    /// <summary>Handles shell.open, shell.resize, and shell.close RPC requests.</summary>
    public async ValueTask<ProtocolMessage> HandleRequestAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        return request.Method switch
        {
            "shell.open" => await OpenAsync(request, cancellationToken).ConfigureAwait(false),
            "shell.resize" => await ResizeAsync(request, cancellationToken).ConfigureAwait(false),
            "shell.close" => await CloseAsync(request, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unknown interactive shell request: {request.Method}")
        };
    }

    /// <summary>Handles client shell.input stream messages.</summary>
    public async ValueTask HandleMessageAsync(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        if (message.Method != "shell.input" || message.RequestId != 0 || message.StreamId == 0)
            throw new InvalidDataException("Invalid interactive shell stream message.");
        var session = GetSession(message.StreamId);
        switch (message.Kind)
        {
            case MessageKind.StreamData:
                if (message.Payload.Length is 0 or > MaxInputFrameBytes)
                    throw new InvalidDataException("Interactive shell input frames must contain 1 to 65536 bytes.");
                await session.WriteInputAsync(message.Payload).ConfigureAwait(false);
                break;
            case MessageKind.StreamEnd:
                if (message.Payload.Length != 0) throw new InvalidDataException("shell.input StreamEnd must have an empty payload.");
                await session.EndInputAsync().ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("Expected shell.input StreamData or StreamEnd.");
        }
    }

    private async ValueTask<ProtocolMessage> OpenAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        if (!_permissions.IsAllowed(_peerId, Capability.Shell))
            throw new UnauthorizedAccessException("Shell access is not granted on this device for this peer.");
        using var json = JsonDocument.Parse(request.Payload);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("columns", out var c) ||
            !root.TryGetProperty("rows", out var r) || !root.TryGetProperty("elevated", out var e) ||
            !c.TryGetInt32(out var columns) || !r.TryGetInt32(out var rows) || e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("shell.open requires integer columns, integer rows, and boolean elevated fields.");
        if (columns is < 1 or > short.MaxValue || rows is < 1 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request), "Terminal dimensions must be from 1 through 32767.");
        var elevated = e.GetBoolean();
        if (elevated && OperatingSystem.IsWindows() && !_permissions.IsAllowed(_peerId, Capability.PrivilegedShell))
            throw new UnauthorizedAccessException("Privileged shell access is not granted on this device for this peer.");
        IInteractiveShellBackend backend = elevated && OperatingSystem.IsWindows()
            ? new WindowsAdminBrokerInteractiveBackend() : _backend;
        if (!backend.IsAvailable) throw new PlatformNotSupportedException("A real PTY/ConPTY backend is unavailable.");

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_sessions.Count + _opening >= MaxSessions) throw new InvalidOperationException("The connection has reached its interactive shell session limit.");
            if (_nextId > uint.MaxValue) throw new InvalidOperationException("No interactive shell session IDs remain for this connection.");
            _opening++;
        }
        IInteractiveShellSession pty;
        try { pty = await backend.StartAsync(elevated, (short)columns, (short)rows, cancellationToken).ConfigureAwait(false); }
        catch { lock (_gate) _opening--; throw; }
        Session session;
        lock (_gate)
        {
            _opening--;
            if (_disposed || _sessions.Count >= MaxSessions || _nextId > uint.MaxValue)
            {
                session = null!;
            }
            else
            {
                var id = (uint)_nextId++;
                session = new Session(id, pty, _send);
                _sessions.Add(id, session);
                session.Start();
            }
        }
        if (session is null)
        {
            await pty.DisposeAsync().ConfigureAwait(false);
            ThrowIfDisposed();
            throw new InvalidOperationException("The connection has reached its interactive shell session limit.");
        }
        return new ProtocolMessage(MessageKind.Response, request.RequestId, request.StreamId, request.Method,
            UInt32Bytes(session.Id));
    }

    private async ValueTask<ProtocolMessage> ResizeAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        if (request.Payload.Length != 8) throw new InvalidDataException("shell.resize payload must be 8 bytes.");
        var id = BinaryPrimitives.ReadUInt32BigEndian(request.Payload.AsSpan(0, 4));
        var columns = BinaryPrimitives.ReadUInt16BigEndian(request.Payload.AsSpan(4, 2));
        var rows = BinaryPrimitives.ReadUInt16BigEndian(request.Payload.AsSpan(6, 2));
        if (id == 0 || columns == 0 || rows == 0 || columns > short.MaxValue || rows > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown session ID or invalid terminal dimensions.");
        await GetSession(id).ResizeAsync((short)columns, (short)rows, cancellationToken).ConfigureAwait(false);
        return EmptyReply(request);
    }

    private async ValueTask<ProtocolMessage> CloseAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Payload.Length != 4) throw new InvalidDataException("shell.close payload must be 4 bytes.");
        var id = BinaryPrimitives.ReadUInt32BigEndian(request.Payload);
        var session = RemoveSession(id);
        await session.DisposeAsync().ConfigureAwait(false);
        return EmptyReply(request);
    }

    private Session GetSession(uint id)
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            return id != 0 && _sessions.TryGetValue(id, out var session)
                ? session : throw new KeyNotFoundException($"Unknown interactive shell session ID {id}.");
        }
    }

    private Session RemoveSession(uint id)
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (id == 0 || !_sessions.Remove(id, out var session))
                throw new KeyNotFoundException($"Unknown interactive shell session ID {id}.");
            return session;
        }
    }

    private static byte[] UInt32Bytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static ProtocolMessage EmptyReply(ProtocolMessage request) =>
        new(MessageKind.Response, request.RequestId, request.StreamId, request.Method, []);

    private void ThrowIfDisposed()
    {
        lock (_gate) ThrowIfDisposedLocked();
    }
    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        Session[] sessions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var session in sessions) await session.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Session(uint id, IInteractiveShellSession pty,
        Func<ProtocolMessage, CancellationToken, ValueTask> send) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _inputLock = new(1, 1);
        private readonly CancellationTokenSource _shutdown = new();
        private Task? _pump;
        private int _inputEnded, _disposed;
        internal uint Id { get; } = id;

        internal void Start() => _pump = PumpAsync();

        internal async Task WriteInputAsync(byte[] data)
        {
            await _inputLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                if (_inputEnded != 0) throw new InvalidOperationException("Interactive shell input has ended.");
                await pty.Input.WriteAsync(data, _shutdown.Token).ConfigureAwait(false);
                await pty.Input.FlushAsync(_shutdown.Token).ConfigureAwait(false);
            }
            finally { _inputLock.Release(); }
        }

        internal async Task EndInputAsync()
        {
            await _inputLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                // Closing ConPTY's input pipe can terminate the shell before queued input
                // is processed. EOF stops further network writes; session cleanup owns the pipe.
                Interlocked.Exchange(ref _inputEnded, 1);
            }
            finally { _inputLock.Release(); }
        }

        internal ValueTask ResizeAsync(short columns, short rows, CancellationToken token) =>
            pty.ResizeAsync(columns, rows, token);

        private async Task PumpAsync()
        {
            var buffer = new byte[MaxInputFrameBytes];
            try
            {
                while (true)
                {
                    var count = await pty.Output.ReadAsync(buffer, _shutdown.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    await send(new ProtocolMessage(MessageKind.StreamData, 0, Id, "shell.output", buffer.AsSpan(0, count).ToArray()), _shutdown.Token).ConfigureAwait(false);
                }
                var exit = await pty.WaitForExitAsync(_shutdown.Token).ConfigureAwait(false);
                var payload = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(payload, exit);
                await send(new ProtocolMessage(MessageKind.Event, 0, Id, "shell.exit", payload), _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _shutdown.Cancel();
            try { await pty.DisposeAsync().ConfigureAwait(false); } finally
            {
                if (_pump is not null) try { await _pump.ConfigureAwait(false); } catch { }
                _shutdown.Dispose(); _inputLock.Dispose();
            }
        }
    }
}
