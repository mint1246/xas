using System.Buffers.Binary;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Transport;

namespace Xas.Cli.Interactive;

/// <summary>Runs an interactive shell over one pinned mutual TLS connection.</summary>
public static class InteractiveShellClient
{
    private const int MaxInputChunkBytes = 64 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs a remote shell and returns its exit code.</summary>
    public static async Task<int> RunAsync(ConfiguredPeer configuredPeer, DeviceIdentity identity,
        PeerTrustStore trustStore, Stream input, Stream output, ushort columns, ushort rows,
        CancellationToken cancellationToken = default,
        Func<(ushort Columns, ushort Rows)>? currentSize = null, bool elevated = false)
    {
        ArgumentNullException.ThrowIfNull(configuredPeer);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trustStore);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead) throw new ArgumentException("Input stream must be readable.", nameof(input));
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        if (columns == 0 || rows == 0)
            throw new ArgumentOutOfRangeException(
                nameof(columns), "Terminal dimensions must be positive.");

        await using var connection = await MutualTlsTransport.ConnectAsync(
            configuredPeer.Host, configuredPeer.Port, identity, trustStore, configuredPeer.DeviceId,
            ConnectTimeout, cancellationToken).ConfigureAwait(false);
        await using var frames = new BinaryFrameConnection(connection.Stream, leaveOpen: true);
        await using var peer = new MultiplexedProtocolPeer(frames, (_, _) =>
            ValueTask.FromException<ProtocolMessage>(
                new NotSupportedException("Interactive shell client does not accept remote requests.")));

        return await RunOnPeerAsync(
            peer, input, output, columns, rows, cancellationToken, currentSize, elevated)
            .ConfigureAwait(false);
    }

    /// <summary>Runs an interactive shell over an already authenticated multiplexed protocol peer.</summary>
    public static async Task<int> RunOnPeerAsync(MultiplexedProtocolPeer peer, Stream input, Stream output,
        ushort columns, ushort rows, CancellationToken cancellationToken = default,
        Func<(ushort Columns, ushort Rows)>? currentSize = null, bool elevated = false)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead) throw new ArgumentException("Input stream must be readable.", nameof(input));
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        if (columns == 0 || rows == 0)
            throw new ArgumentOutOfRangeException(
                nameof(columns), "Terminal dimensions must be positive.");

        var exitCompletion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        uint sessionId = 0;
        var earlyMessages = new EarlyShellMessages();
        using var sessionGate = new SemaphoreSlim(1, 1);
        peer.MessageReceived += async message =>
        {
            if (message.Method is not ("shell.output" or "shell.exit")) return;
            await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RouteShellMessageAsync(
                    message, sessionId, earlyMessages, output, exitCompletion, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally { sessionGate.Release(); }
        };

        var openPayload = JsonSerializer.SerializeToUtf8Bytes(new OpenRequest(columns, rows, elevated));
        var openReply = await peer.RequestAsync(
            "shell.open", openPayload, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (openReply.Payload.Length != sizeof(uint))
            throw new InvalidDataException("Invalid shell.open response.");
        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            sessionId = BinaryPrimitives.ReadUInt32BigEndian(openReply.Payload);
            if (sessionId == 0)
                throw new InvalidDataException("Remote allocated an invalid shell session id.");
            while (earlyMessages.TryDequeue(out var message))
                await HandleShellMessageAsync(
                    message, sessionId, output, exitCompletion, cancellationToken)
                    .ConfigureAwait(false);
        }
        finally { sessionGate.Release(); }

        using var inputShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputToken = inputShutdown.Token;
        // A synchronous Unix terminal handle can block inside ReadAsync before its
        // first await. Keep that read off the protocol event loop.
        var inputTask = Task.Run(() => PumpInputAsync(input, peer, sessionId, inputToken), inputToken);
        var resizeTask = currentSize is null ? Task.Delay(Timeout.Infinite, inputShutdown.Token)
            : PumpResizeAsync(peer, sessionId, columns, rows, currentSize, inputShutdown.Token);
        try
        {
            var completed = await Task.WhenAny(
                exitCompletion.Task, inputTask, peer.Completion, resizeTask).ConfigureAwait(false);
            if (completed == peer.Completion)
                throw new IOException(
                    "The interactive shell connection closed before the remote exit event.");
            if (completed == resizeTask)
            {
                await resizeTask.ConfigureAwait(false);
                throw new IOException("Terminal resize monitoring stopped unexpectedly.");
            }
            if (completed == inputTask)
            {
                await inputTask.ConfigureAwait(false);
                // Input EOF is not shell completion; continue waiting for shell.exit.
                return await WaitForExitAsync(
                    exitCompletion.Task, peer.Completion, resizeTask).ConfigureAwait(false);
            }
            var code = await exitCompletion.Task.ConfigureAwait(false);
            inputShutdown.Cancel();
            // Console input reads may not observe cancellation until the user types again.
            // Do not hold up the remote exit or terminal-mode restoration for that read.
            _ = inputTask.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return code;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            inputShutdown.Cancel();
            try
            {
                await peer.RequestAsync(
                    "shell.close", EncodeSessionId(sessionId),
                    cancellationToken: CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception) { /* Closing the TLS connection also reaps the session. */ }
            throw;
        }
        finally
        {
            inputShutdown.Cancel();
            if (sessionId != 0 &&
                !cancellationToken.IsCancellationRequested &&
                exitCompletion.Task.IsCompleted)
            {
                try
                {
                    await peer.RequestAsync(
                        "shell.close", EncodeSessionId(sessionId),
                        cancellationToken: CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception) { /* The server may reap on exit or disconnect. */ }
            }
        }
    }

    private static async Task<int> WaitForExitAsync(
        Task<int> exitTask,
        Task connectionTask,
        Task resizeTask)
    {
        var completed = await Task.WhenAny(exitTask, connectionTask, resizeTask).ConfigureAwait(false);
        if (completed == connectionTask)
            throw new IOException(
                "The interactive shell connection closed before the remote exit event.");
        if (completed == resizeTask)
        {
            await resizeTask.ConfigureAwait(false);
            throw new IOException("Terminal resize monitoring stopped unexpectedly.");
        }
        return await exitTask.ConfigureAwait(false);
    }

    private static async Task RouteShellMessageAsync(
        ProtocolMessage message,
        uint sessionId,
        EarlyShellMessages earlyMessages,
        Stream output,
        TaskCompletionSource<int> exitCompletion,
        CancellationToken cancellationToken)
    {
        if (sessionId == 0)
        {
            earlyMessages.Enqueue(message);
            return;
        }
        await HandleShellMessageAsync(
            message, sessionId, output, exitCompletion, cancellationToken).ConfigureAwait(false);
    }

    private sealed class EarlyShellMessages
    {
        private const int MaxBufferedBytes = 1024 * 1024;
        private readonly Queue<ProtocolMessage> _messages = new();
        private int _bufferedBytes;

        public void Enqueue(ProtocolMessage message)
        {
            _bufferedBytes = checked(_bufferedBytes + message.Payload.Length);
            if (_bufferedBytes > MaxBufferedBytes)
                throw new InvalidDataException(
                    "Too much shell output arrived before the open response.");
            _messages.Enqueue(message);
        }

        public bool TryDequeue(out ProtocolMessage message) => _messages.TryDequeue(out message!);
    }

    private static async Task PumpResizeAsync(MultiplexedProtocolPeer peer, uint sessionId,
        ushort originalColumns, ushort originalRows, Func<(ushort Columns, ushort Rows)> currentSize,
        CancellationToken cancellationToken)
    {
        var previous = (Columns: originalColumns, Rows: originalRows);
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            var next = currentSize();
            if (next.Columns == 0 || next.Rows == 0 || next == previous) continue;
            var payload = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), sessionId);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4, 2), next.Columns);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), next.Rows);
            await peer.RequestAsync(
                "shell.resize", payload, cancellationToken: cancellationToken).ConfigureAwait(false);
            previous = next;
        }
    }

    private static async Task PumpInputAsync(Stream input, MultiplexedProtocolPeer peer, uint sessionId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxInputChunkBytes];
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var payload = buffer.AsSpan(0, count).ToArray();
            var message = new ProtocolMessage(
                MessageKind.StreamData, 0, sessionId, "shell.input", payload);
            await peer.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        var endMessage = new ProtocolMessage(
            MessageKind.StreamEnd, 0, sessionId, "shell.input", Array.Empty<byte>());
        await peer.SendAsync(endMessage, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask HandleShellMessageAsync(ProtocolMessage message, uint sessionId,
        Stream output, TaskCompletionSource<int> exit, CancellationToken cancellationToken)
    {
        if (message.StreamId != sessionId)
            throw new InvalidDataException("Unexpected interactive shell session id.");
        switch (message.Kind)
        {
            case MessageKind.StreamData when message.Method == "shell.output":
                if (message.Payload.Length > MaxInputChunkBytes)
                    throw new InvalidDataException("Shell output frame exceeds 64 KiB.");
                await output.WriteAsync(message.Payload, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                break;
            case MessageKind.Event when message.Method == "shell.exit":
                exit.TrySetResult(ReadShellExitCode(message.Payload));
                break;
            default:
                throw new InvalidDataException(
                    $"Unexpected interactive shell message '{message.Method}'.");
        }
    }

    private static int ReadShellExitCode(byte[] payload)
    {
        if (payload.Length != sizeof(int))
            throw new InvalidDataException("Invalid shell exit payload.");
        return BinaryPrimitives.ReadInt32BigEndian(payload);
    }

    private static byte[] EncodeSessionId(uint id)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, id);
        return bytes;
    }

    private sealed record OpenRequest(ushort columns, ushort rows, bool elevated);
}
