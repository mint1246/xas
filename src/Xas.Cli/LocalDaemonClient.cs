using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.LocalIpc;
using Xas.Core.Protocol;
using Xas.Core.Services;
using Xas.Cli.Interactive;
using Xas.Cli.Terminal;
using Xas.Cli.FileTransfer;
using Xas.Input;

namespace Xas.Cli;

/// <summary>Thin command-line client for the user-session daemon.</summary>
public sealed class LocalDaemonClient(Stream input, Stream output, Stream error) : IXasClient, IDisposable
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private Stream? _stream;
    private MultiplexedProtocolPeer? _peer;
    private bool _disposed;
    private bool _bound;

    public async Task<IReadOnlyList<KnownDeviceInfo>> ListDevicesAsync(
        CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.Devices, [], cancellationToken)
            .ConfigureAwait(false);
        return DeserializeRequired<KnownDeviceInfo[]>(
            reply, "The daemon returned an invalid device list.");
    }

    public async Task<DeviceInfo?> GetDeviceInfoAsync(
        string? deviceId, CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.Info, new LocalTarget(deviceId), cancellationToken).ConfigureAwait(false);
        return DeserializeReply<DeviceInfo?>(reply);
    }

    public async Task SetDefaultDeviceAsync(string deviceId, CancellationToken cancellationToken) =>
        _ = await RequestJsonAsync(
            LocalIpcProtocol.SetDefault, new LocalTarget(deviceId), cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> PingAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.Ping, new LocalTarget(deviceId), cancellationToken).ConfigureAwait(false);
        if (reply.Payload.Length != 1 || reply.Payload[0] > 1)
            throw new InvalidDataException("The daemon returned an invalid ping response.");
        return reply.Payload[0] == 1;
    }

    public async Task<string> GetUiUrlAsync(CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.UiInfo, [], cancellationToken)
            .ConfigureAwait(false);
        var info = DeserializeRequired<LocalUiInfo>(
            reply, "The daemon returned invalid UI information.");
        if (!Uri.TryCreate(info.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
            throw new InvalidDataException("The daemon returned a non-loopback UI URL.");
        return uri.AbsoluteUri;
    }

    public async Task<LocalPairPending> BeginPairingAsync(
        string host, int controlPort, string? expectedDeviceId,
        CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.PairBegin,
            new LocalPairBegin(host, controlPort, expectedDeviceId),
            cancellationToken).ConfigureAwait(false);
        return DeserializeRequired<LocalPairPending>(
            reply, "The daemon returned an invalid pairing request.");
    }

    public async Task<IReadOnlyList<LocalPairCandidate>> ListPairingCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.PairCandidates, [], cancellationToken)
            .ConfigureAwait(false);
        return DeserializeRequired<LocalPairCandidate[]>(
            reply, "The daemon returned an invalid pairing candidate list.");
    }

    public async Task<LocalPairPending> BeginDiscoveredPairingAsync(
        string query, CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.PairDiscover, new LocalPairTarget(query), cancellationToken)
            .ConfigureAwait(false);
        return DeserializeRequired<LocalPairPending>(
            reply, "The daemon returned an invalid pairing request.");
    }

    public async Task<IReadOnlyList<LocalPairPending>> ListPairingsAsync(
        CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(LocalIpcProtocol.PairList, [], cancellationToken)
            .ConfigureAwait(false);
        return DeserializeRequired<LocalPairPending[]>(
            reply, "The daemon returned an invalid pairing list.");
    }

    public async Task ApprovePairingAsync(
        string pairingId, bool approve, CancellationToken cancellationToken,
        PairPermissionPreset preset = PairPermissionPreset.Personal)
    {
        _ = await RequestJsonAsync(
            LocalIpcProtocol.PairApprove,
            new LocalPairDecision(pairingId, approve, preset),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RunShellAsync(
        ShellRequest request, string? deviceId, CancellationToken cancellationToken)
    {
        using var outputCodePage = TerminalMode.EnterUtf8Output();
        var peer = await GetPeerAsync(cancellationToken).ConfigureAwait(false);
        await BindAsync(peer, deviceId, cancellationToken).ConfigureAwait(false);
        var sessionId = 0u;
        var earlyMessages = new EarlyCommandMessages();
        var eventGate = new SemaphoreSlim(1, 1);
        var exitCompletion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask OnMessage(ProtocolMessage message)
        {
            if (message.Method is not (
                ShellExecWire.Stdout or ShellExecWire.Stderr or ShellExecWire.Exit or ShellExecWire.Error))
                return;
            await eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RouteCommandMessageAsync(
                    message, sessionId, earlyMessages, exitCompletion, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                eventGate.Release();
            }
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
                while (earlyMessages.TryDequeue(out var message))
                    await HandleShellMessageAsync(
                        message, sessionId, exitCompletion, cancellationToken).ConfigureAwait(false);
            }
            finally { eventGate.Release(); }

            using var inputCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pumpToken = inputCancellation.Token;
            // Reads from an attached Unix terminal can block synchronously even through
            // ReadAsync. Let exit events complete independently of that stdin read.
            var inputTask = Task.Run(() => PumpStdinAsync(peer, sessionId, pumpToken), pumpToken);
            var completed = await Task.WhenAny(
                exitCompletion.Task, inputTask, peer.Completion).ConfigureAwait(false);
            if (completed == inputTask)
            {
                await inputTask.ConfigureAwait(false);
                completed = await Task.WhenAny(exitCompletion.Task, peer.Completion)
                    .ConfigureAwait(false);
            }
            if (completed == peer.Completion)
                throw new IOException("The local daemon disconnected during the command.");
            var code = await exitCompletion.Task.ConfigureAwait(false);
            inputCancellation.Cancel();
            _ = inputTask.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return code;
        }
        catch (RemoteProtocolException ex) { throw new XasClientException(ex.Message); }
        finally
        {
            peer.MessageReceived -= OnMessage;
            await CloseSessionAsync(peer, sessionId).ConfigureAwait(false);
            eventGate.Dispose();
        }
    }

    private static async Task CloseSessionAsync(MultiplexedProtocolPeer peer, uint sessionId)
    {
        if (sessionId == 0)
            return;
        try
        {
            await peer.RequestAsync(
                ShellExecWire.Close,
                ShellExecWire.EncodeSessionId(sessionId),
                cancellationToken: CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch
        {
            // Daemon also reaps sessions on local disconnect.
        }
    }

    private async Task RouteCommandMessageAsync(
        ProtocolMessage message,
        uint sessionId,
        EarlyCommandMessages earlyMessages,
        TaskCompletionSource<int> exitCompletion,
        CancellationToken cancellationToken)
    {
        if (sessionId == 0)
        {
            earlyMessages.Enqueue(message);
            return;
        }
        await HandleShellMessageAsync(
            message, sessionId, exitCompletion, cancellationToken).ConfigureAwait(false);
    }

    private sealed class EarlyCommandMessages
    {
        private const int MaxBufferedBytes = 1024 * 1024;
        private readonly Queue<ProtocolMessage> _messages = new();
        private int _bufferedBytes;

        public void Enqueue(ProtocolMessage message)
        {
            _bufferedBytes = checked(_bufferedBytes + message.Payload.Length);
            if (_bufferedBytes > MaxBufferedBytes)
                throw new InvalidDataException(
                    "Too much command output arrived before the open response.");
            _messages.Enqueue(message);
        }

        public bool TryDequeue(out ProtocolMessage message) => _messages.TryDequeue(out message!);
    }

    private async Task PumpStdinAsync(
        MultiplexedProtocolPeer peer, uint sessionId, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            var dataMessage = new ProtocolMessage(
                MessageKind.StreamData,
                0,
                sessionId,
                LocalIpcProtocol.ShellStdin,
                buffer.AsSpan(0, count).ToArray());
            await peer.SendAsync(dataMessage, token).ConfigureAwait(false);
        }
        await peer.SendAsync(new ProtocolMessage(MessageKind.StreamEnd, 0, sessionId,
            LocalIpcProtocol.ShellStdin, []), token).ConfigureAwait(false);
    }

    private async ValueTask HandleShellMessageAsync(ProtocolMessage message, uint sessionId,
        TaskCompletionSource<int> exit, CancellationToken token)
    {
        if (message.StreamId != sessionId)
            throw new InvalidDataException("Unexpected local shell session ID.");
        var isOutputStream = message.Method is
            LocalIpcProtocol.ShellStdout or LocalIpcProtocol.ShellStderr;
        if (message.Kind == MessageKind.StreamData && isOutputStream)
        {
            var destination = message.Method == LocalIpcProtocol.ShellStdout ? output : error;
            await destination.WriteAsync(message.Payload, token).ConfigureAwait(false);
            return;
        }
        if (message.Kind == MessageKind.StreamEnd && isOutputStream)
            return;
        if (message.Kind == MessageKind.Event &&
            message.Method == LocalIpcProtocol.ShellExit && message.Payload.Length == sizeof(int))
        {
            if (message.StreamId != sessionId)
                throw new InvalidDataException("Unexpected local shell exit session ID.");
            exit.TrySetResult(ShellExecWire.DecodeExitCode(message.Payload));
            return;
        }
        if (message.Kind == MessageKind.Event && message.Method == LocalIpcProtocol.ShellError)
        {
            if (message.StreamId != sessionId)
                throw new InvalidDataException("Unexpected local shell error session ID.");
            var errorMessage = System.Text.Encoding.UTF8.GetString(message.Payload);
            exit.TrySetException(new XasClientException(errorMessage));
            return;
        }
        throw new InvalidDataException("Unexpected local shell message.");
    }

    private async Task<ProtocolMessage> RequestAsync(
        string method, byte[] payload, CancellationToken token)
    {
        try
        {
            var peer = await GetPeerAsync(token).ConfigureAwait(false);
            return await peer.RequestAsync(method, payload, cancellationToken: token)
                .ConfigureAwait(false);
        }
        catch (RemoteProtocolException ex) { throw new XasClientException(ex.Message); }
    }

    private Task<ProtocolMessage> RequestJsonAsync<T>(
        string method, T request, CancellationToken token) =>
        RequestAsync(
            method,
            JsonSerializer.SerializeToUtf8Bytes(request, LocalIpcProtocol.Json),
            token);

    private static T? DeserializeReply<T>(ProtocolMessage reply) =>
        JsonSerializer.Deserialize<T>(reply.Payload, LocalIpcProtocol.Json);

    private static T DeserializeRequired<T>(ProtocolMessage reply, string errorMessage) =>
        DeserializeReply<T>(reply)
        ?? throw new InvalidDataException(errorMessage);

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
                ValueTask.FromException<ProtocolMessage>(
                    new NotSupportedException("The local CLI does not accept requests.")));
            return _peer;
        }
        finally { _connectionGate.Release(); }
    }

    public async Task<int> RunInteractiveAsync(
        string? deviceId, bool elevated, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new XasClientException("Interactive shell requires an attached terminal.");
        using var terminal = TerminalMode.Enter();
        await using var connection = await LocalIpcConnection.ConnectAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);
        using var terminalInput = TerminalMode.OpenInputStream();
        return await InteractiveShellClient.RunOnPeerAsync(
            connection.Peer,
            terminalInput,
            output,
            (ushort)terminal.Columns,
            (ushort)terminal.Rows,
            cancellationToken,
            TerminalMode.CurrentSize,
            elevated).ConfigureAwait(false);
    }
    public async Task<int> CopyAsync(
        string source,
        string destination,
        bool recursive,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var config = new LocalConfiguration();
        var target = FileCopyClient.ResolveTargetDeviceId(source, destination, config);
        await using var connection = await LocalIpcConnection.ConnectAsync(target, cancellationToken)
            .ConfigureAwait(false);
        using var progress = new StreamWriter(error, Console.Error.Encoding, 1024, leaveOpen: true);
        await FileCopyClient.CopyWithPeerAsync(source, destination, recursive, overwrite, config,
            connection.Peer, progress, cancellationToken).ConfigureAwait(false);
        return 0;
    }
    public async Task<int> SyncClipboardAsync(
        bool push, string? deviceId, CancellationToken cancellationToken)
    {
        var method = push ? LocalIpcProtocol.ClipboardPush : LocalIpcProtocol.ClipboardPull;
        _ = await RequestJsonAsync(method, new LocalTarget(deviceId), cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }

    public async Task<int> WatchClipboardAsync(
        string? deviceId, CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.ClipboardStatus, new LocalTarget(deviceId), cancellationToken)
            .ConfigureAwait(false);
        var status = DeserializeRequired<LocalClipboardStatus>(
            reply, "The daemon returned an invalid clipboard status.");
        using var writer = new StreamWriter(
            output, Console.OutputEncoding, 1024, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(status.Message).ConfigureAwait(false);
        return status.Active ? 0 : 1;
    }

    public async Task<IReadOnlyList<LocalRemoteVolumeInfo>> ListRemoteVolumesAsync(string? deviceId,
        CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.StorageVolumes, new LocalTarget(deviceId), cancellationToken)
            .ConfigureAwait(false);
        return DeserializeRequired<LocalRemoteVolumeInfo[]>(
            reply, "The daemon returned an invalid remote volume list.");
    }

    public async Task<IReadOnlyList<LocalRemoteVolumeInfo>> ListRemoteMountsAsync(string? deviceId,
        CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.StorageMounts, new LocalTarget(deviceId), cancellationToken)
            .ConfigureAwait(false);
        return DeserializeRequired<LocalRemoteVolumeInfo[]>(
            reply, "The daemon returned an invalid remote mount list.");
    }

    public async Task<LocalRemoteVolumeInfo> MountRemoteVolumeAsync(string? deviceId, string volume,
        CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.StorageMount,
            new LocalStorageVolumeTarget(deviceId, volume),
            cancellationToken).ConfigureAwait(false);
        return DeserializeRequired<LocalRemoteVolumeInfo>(
            reply, "The daemon returned an invalid remote mount response.");
    }

    public async Task<bool> UnmountRemoteVolumeAsync(string? deviceId, string volume,
        CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.StorageUnmount,
            new LocalStorageVolumeTarget(deviceId, volume),
            cancellationToken).ConfigureAwait(false);
        return DeserializeReply<bool>(reply);
    }

    public async Task EjectRemoteVolumeAsync(
        string? deviceId, string volume, CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.StorageEject,
            new LocalStorageVolumeTarget(deviceId, volume),
            cancellationToken).ConfigureAwait(false);
        if (reply.Payload.Length != 0)
            throw new InvalidDataException("The daemon returned an invalid remote eject response.");
    }

    public async Task<string> RevokeAsync(string deviceId, CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.Revoke, new LocalTarget(deviceId), cancellationToken).ConfigureAwait(false);
        return DeserializeRequired<string>(reply, "The daemon returned an invalid revoke response.");
    }
    public async Task<string> UpdateEndpointAsync(string deviceId, string host, int port,
        CancellationToken cancellationToken)
    {
        var reply = await RequestJsonAsync(
            LocalIpcProtocol.Endpoint,
            new LocalEndpointUpdate(deviceId, host, port),
            cancellationToken).ConfigureAwait(false);
        return DeserializeRequired<string>(reply, "The daemon returned an invalid endpoint response.");
    }
    public async Task<int> RunInputAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var configured = new LocalConfiguration().Resolve(deviceId)
            ?? throw new InvalidOperationException("No matching configured device.");
        await using var connection = await LocalIpcConnection.ConnectAsync(
            configured.DeviceId, cancellationToken).ConfigureAwait(false);
        await InputControlClient.RunOnPeerAsync(
            connection.Peer, configured.DeviceId, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _peer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stream?.Dispose();
        _connectionGate.Dispose();
    }
}
