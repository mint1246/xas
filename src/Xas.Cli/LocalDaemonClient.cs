using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.LocalIpc;
using Xas.Core.Protocol;
using Xas.Core.Services;
using Xas.Cli.Interactive;
using Xas.Cli.Terminal;
using Xas.Cli.FileTransfer;

namespace Xas.Cli;

/// <summary>Thin command-line client for the user-session daemon.</summary>
public sealed class LocalDaemonClient(Stream input, Stream output, Stream error) : IXasClient, IDisposable
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private Stream? _stream;
    private MultiplexedProtocolPeer? _peer;
    private bool _disposed;
    private bool _bound;

    public async Task<IReadOnlyList<KnownDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.Devices, [], cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<KnownDeviceInfo[]>(reply.Payload, LocalIpcProtocol.Json)
            ?? throw new InvalidDataException("The daemon returned an invalid device list.");
    }

    public async Task<DeviceInfo?> GetDeviceInfoAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.Info,
            JsonSerializer.SerializeToUtf8Bytes(new LocalTarget(deviceId), LocalIpcProtocol.Json), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DeviceInfo?>(reply.Payload, LocalIpcProtocol.Json);
    }

    public async Task SetDefaultDeviceAsync(string deviceId, CancellationToken cancellationToken) =>
        _ = await RequestAsync(LocalIpcProtocol.SetDefault,
            JsonSerializer.SerializeToUtf8Bytes(new LocalTarget(deviceId), LocalIpcProtocol.Json), cancellationToken).ConfigureAwait(false);

    public async Task<bool> PingAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.Ping,
            JsonSerializer.SerializeToUtf8Bytes(new LocalTarget(deviceId), LocalIpcProtocol.Json), cancellationToken).ConfigureAwait(false);
        if (reply.Payload.Length != 1 || reply.Payload[0] > 1) throw new InvalidDataException("The daemon returned an invalid ping response.");
        return reply.Payload[0] == 1;
    }

    public async Task<LocalPairPending> BeginPairingAsync(string host, int controlPort, string? expectedDeviceId,
        CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.PairBegin,
            JsonSerializer.SerializeToUtf8Bytes(new LocalPairBegin(host, controlPort, expectedDeviceId), LocalIpcProtocol.Json), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<LocalPairPending>(reply.Payload, LocalIpcProtocol.Json)
            ?? throw new InvalidDataException("The daemon returned an invalid pairing request.");
    }

    public async Task<IReadOnlyList<LocalPairCandidate>> ListPairingCandidatesAsync(CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.PairCandidates, [], cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<LocalPairCandidate[]>(reply.Payload, LocalIpcProtocol.Json)
            ?? throw new InvalidDataException("The daemon returned an invalid pairing candidate list.");
    }

    public async Task<LocalPairPending> BeginDiscoveredPairingAsync(string query, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.PairDiscover,
            JsonSerializer.SerializeToUtf8Bytes(new LocalPairTarget(query), LocalIpcProtocol.Json), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<LocalPairPending>(reply.Payload, LocalIpcProtocol.Json)
            ?? throw new InvalidDataException("The daemon returned an invalid pairing request.");
    }

    public async Task<IReadOnlyList<LocalPairPending>> ListPairingsAsync(CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.PairList, [], cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<LocalPairPending[]>(reply.Payload, LocalIpcProtocol.Json)
            ?? throw new InvalidDataException("The daemon returned an invalid pairing list.");
    }

    public async Task ApprovePairingAsync(string pairingId, bool approve, CancellationToken cancellationToken,
        PairPermissionPreset preset = PairPermissionPreset.Personal)
    {
        _ = await RequestAsync(LocalIpcProtocol.PairApprove,
            JsonSerializer.SerializeToUtf8Bytes(new LocalPairDecision(pairingId, approve, preset), LocalIpcProtocol.Json), cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RunShellAsync(ShellRequest request, string? deviceId, CancellationToken cancellationToken)
    {
        using var outputCodePage = TerminalMode.EnterUtf8Output();
        var peer = await GetPeerAsync(cancellationToken).ConfigureAwait(false);
        await BindAsync(peer, deviceId, cancellationToken).ConfigureAwait(false);
        var sessionId = 0u;
        var early = new Queue<ProtocolMessage>();
        var earlyBytes = 0;
        var eventGate = new SemaphoreSlim(1, 1);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask OnMessage(ProtocolMessage message)
        {
            await eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (sessionId == 0)
                {
                    earlyBytes = checked(earlyBytes + message.Payload.Length);
                    if (earlyBytes > 1024 * 1024) throw new InvalidDataException("Too much command output arrived before the open response.");
                    early.Enqueue(message);
                }
                else await HandleShellMessageAsync(message, sessionId, exit, cancellationToken).ConfigureAwait(false);
            }
            finally { eventGate.Release(); }
        }
        peer.MessageReceived += OnMessage;
        try
        {
            var reply = await peer.RequestAsync(ShellExecWire.Open, ShellWire.EncodeRequest(request),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                sessionId = ShellExecWire.DecodeSessionId(reply.Payload);
                while (early.TryDequeue(out var message))
                    await HandleShellMessageAsync(message, sessionId, exit, cancellationToken).ConfigureAwait(false);
            }
            finally { eventGate.Release(); }

            using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var inputTask = PumpStdinAsync(peer, sessionId, pumpStop.Token);
            var completed = await Task.WhenAny(exit.Task, inputTask, peer.Completion).ConfigureAwait(false);
            if (completed == inputTask)
            {
                await inputTask.ConfigureAwait(false);
                completed = await Task.WhenAny(exit.Task, peer.Completion).ConfigureAwait(false);
            }
            if (completed == peer.Completion) throw new IOException("The local daemon disconnected during the command.");
            var code = await exit.Task.ConfigureAwait(false);
            pumpStop.Cancel();
            _ = inputTask.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return code;
        }
        catch (RemoteProtocolException ex) { throw new XasClientException(ex.Message); }
        finally
        {
            peer.MessageReceived -= OnMessage;
            if (sessionId != 0)
            {
                try { await peer.RequestAsync(ShellExecWire.Close, ShellExecWire.EncodeSessionId(sessionId),
                    cancellationToken: CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* Daemon also reaps sessions on local disconnect. */ }
            }
            eventGate.Dispose();
        }
    }

    private async Task PumpStdinAsync(MultiplexedProtocolPeer peer, uint sessionId, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            await peer.SendAsync(new ProtocolMessage(MessageKind.StreamData, 0, sessionId,
                LocalIpcProtocol.ShellStdin, buffer.AsSpan(0, count).ToArray()), token).ConfigureAwait(false);
        }
        await peer.SendAsync(new ProtocolMessage(MessageKind.StreamEnd, 0, sessionId,
            LocalIpcProtocol.ShellStdin, []), token).ConfigureAwait(false);
    }

    private async ValueTask HandleShellMessageAsync(ProtocolMessage message, uint sessionId,
        TaskCompletionSource<int> exit, CancellationToken token)
    {
        if (message.StreamId != sessionId) throw new InvalidDataException("Unexpected local shell session ID.");
        if (message.Kind == MessageKind.StreamData && message.Method is LocalIpcProtocol.ShellStdout or LocalIpcProtocol.ShellStderr)
        {
            var destination = message.Method == LocalIpcProtocol.ShellStdout ? output : error;
            await destination.WriteAsync(message.Payload, token).ConfigureAwait(false);
            return;
        }
        if (message.Kind == MessageKind.StreamEnd && message.Method is LocalIpcProtocol.ShellStdout or LocalIpcProtocol.ShellStderr)
            return;
        if (message.Kind == MessageKind.Event && message.Method == LocalIpcProtocol.ShellExit && message.Payload.Length == 4)
        {
            if (message.StreamId != sessionId) throw new InvalidDataException("Unexpected local shell exit session ID.");
            exit.TrySetResult(ShellExecWire.DecodeExitCode(message.Payload));
            return;
        }
        if (message.Kind == MessageKind.Event && message.Method == LocalIpcProtocol.ShellError)
        {
            if (message.StreamId != sessionId) throw new InvalidDataException("Unexpected local shell error session ID.");
            exit.TrySetException(new XasClientException(System.Text.Encoding.UTF8.GetString(message.Payload)));
            return;
        }
        throw new InvalidDataException("Unexpected local shell message.");
    }

    private async Task<ProtocolMessage> RequestAsync(string method, byte[] payload, CancellationToken token)
    {
        try { return await (await GetPeerAsync(token).ConfigureAwait(false)).RequestAsync(method, payload, cancellationToken: token).ConfigureAwait(false); }
        catch (RemoteProtocolException ex) { throw new XasClientException(ex.Message); }
    }

    private async Task BindAsync(MultiplexedProtocolPeer peer, string? deviceId, CancellationToken token)
    {
        if (_bound) return;
        _ = await peer.RequestAsync(LocalIpcProtocol.Bind,
            JsonSerializer.SerializeToUtf8Bytes(new LocalTarget(deviceId), LocalIpcProtocol.Json),
            cancellationToken: token).ConfigureAwait(false);
        _bound = true;
    }

    private async Task<MultiplexedProtocolPeer> GetPeerAsync(CancellationToken token)
    {
        if (_peer is not null) return _peer;
        await _connectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_peer is not null) return _peer;
            _stream = await LocalIpcEndpoint.ConnectAsync(token).ConfigureAwait(false);
            var frames = new BinaryFrameConnection(_stream, leaveOpen: true);
            _peer = new MultiplexedProtocolPeer(frames, (_, _) =>
                ValueTask.FromException<ProtocolMessage>(new NotSupportedException("The local CLI does not accept requests.")));
            return _peer;
        }
        finally { _connectionGate.Release(); }
    }

    public async Task<int> RunInteractiveAsync(string? deviceId, bool elevated, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new XasClientException("Interactive shell requires an attached terminal.");
        using var terminal = TerminalMode.Enter();
        await using var connection = await LocalIpcConnection.ConnectAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return await InteractiveShellClient.RunOnPeerAsync(connection.Peer, Console.OpenStandardInput(), output,
            (ushort)terminal.Columns, (ushort)terminal.Rows, cancellationToken, TerminalMode.CurrentSize, elevated).ConfigureAwait(false);
    }
    public async Task<int> CopyAsync(string source, string destination, bool recursive, bool overwrite, CancellationToken cancellationToken)
    {
        var config = new LocalConfiguration();
        var target = FileCopyClient.ResolveTargetDeviceId(source, destination, config);
        await using var connection = await LocalIpcConnection.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        using var progress = new StreamWriter(error, Console.Error.Encoding, 1024, leaveOpen: true);
        await FileCopyClient.CopyWithPeerAsync(source, destination, recursive, overwrite, config,
            connection.Peer, progress, cancellationToken).ConfigureAwait(false);
        return 0;
    }
    public Task<int> SyncClipboardAsync(bool push, string? deviceId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Clipboard forwarding through the local daemon is not available yet.");
    public Task<int> WatchClipboardAsync(string? deviceId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Clipboard forwarding through the local daemon is not available yet.");
    public Task<int> RunInputAsync(string? deviceId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Manual input forwarding through the local daemon is not available yet.");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _peer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stream?.Dispose();
        _connectionGate.Dispose();
    }
}
