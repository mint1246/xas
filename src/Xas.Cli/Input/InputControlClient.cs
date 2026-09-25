using System.Buffers.Binary;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Transport;

namespace Xas.Cli.Input;

/// <summary>Manual Windows input takeover over one authenticated connection.</summary>
public static class InputControlClient
{
    public static async Task RunAsync(ConfiguredPeer configured, DeviceIdentity identity,
        PeerTrustStore trust, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configured);
        if (!WindowsInputCapture.IsAvailable)
            throw new PlatformNotSupportedException("Manual input capture requires a Windows user session.");
        await using var connection = await MutualTlsTransport.ConnectAsync(configured.Host, configured.Port,
            identity, trust, configured.DeviceId, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        await using var frames = new BinaryFrameConnection(connection.Stream, leaveOpen: true);
        await using var peer = new MultiplexedProtocolPeer(frames, (_, _) =>
            ValueTask.FromException<ProtocolMessage>(new NotSupportedException("The input controller does not accept remote requests.")));
        var infoReply = await peer.RequestAsync("device.info", [], cancellationToken: cancellationToken).ConfigureAwait(false);
        DeviceInfo? info;
        try { info = JsonSerializer.Deserialize<DeviceInfo>(infoReply.Payload); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid device.info response.", ex); }
        if (info?.DeviceId != configured.DeviceId) throw new InvalidDataException("Device identity changed during input setup.");
        if (!info.Capabilities.Any(c => c.Capability == Capability.Input && c.Version >= 1))
            throw new NotSupportedException("The remote device has no available input injection backend.");

        var opened = await peer.RequestAsync("input.open", [], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (opened.Payload.Length != 4) throw new InvalidDataException("Invalid input.open response.");
        var sessionId = BinaryPrimitives.ReadUInt32BigEndian(opened.Payload);
        if (sessionId == 0) throw new InvalidDataException("The remote input session ID was zero.");

        try
        {
            Console.Error.WriteLine("Remote input is active. Press Ctrl+Alt+Esc to return control locally.");
            await using var capture = await WindowsInputCapture.StartAsync(cancellationToken).ConfigureAwait(false);
            using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sendTask = SendEventsAsync(peer, capture, sessionId, active.Token);
            var heartbeatTask = SendHeartbeatAsync(peer, sessionId, active.Token);
            var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
            var first = await Task.WhenAny(capture.Completion, sendTask, heartbeatTask, peer.Completion, cancelled).ConfigureAwait(false);
            active.Cancel();
            try { await capture.DisposeAsync().ConfigureAwait(false); }
            catch when (first != capture.Completion) { /* Observe the primary send/connection failure below. */ }
            if (first == cancelled) cancellationToken.ThrowIfCancellationRequested();
            if (first == peer.Completion) throw new IOException("The remote input connection closed.");
            if (first == sendTask) await sendTask.ConfigureAwait(false);
            if (first == heartbeatTask) await heartbeatTask.ConfigureAwait(false);
            await capture.Completion.ConfigureAwait(false);
            await ObserveCancelledAsync(sendTask).ConfigureAwait(false);
            await ObserveCancelledAsync(heartbeatTask).ConfigureAwait(false);
        }
        finally
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(payload, sessionId);
            try
            {
                await peer.RequestAsync("input.close", payload, cancellationToken: CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception) { /* Closing the connection also releases the server lease. */ }
        }
    }

    private static async Task SendEventsAsync(MultiplexedProtocolPeer peer, WindowsInputCapture capture,
        uint sessionId, CancellationToken token)
    {
        var batch = new List<InputEvent>(InputWire.MaxEventsPerFrame);
        await foreach (var input in capture.Events.ReadAllAsync(token).ConfigureAwait(false))
        {
            batch.Add(input);
            while (batch.Count < InputWire.MaxEventsPerFrame && capture.Events.TryRead(out var next))
                batch.Add(next);
            await SendWithTimeoutAsync(peer, sessionId, batch, token).ConfigureAwait(false);
            batch.Clear();
        }
    }

    private static async Task SendHeartbeatAsync(MultiplexedProtocolPeer peer, uint sessionId,
        CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            await SendWithTimeoutAsync(peer, sessionId, [new InputEvent(InputEventKind.KeepAlive)], token)
                .ConfigureAwait(false);
        }
    }

    private static async Task SendWithTimeoutAsync(MultiplexedProtocolPeer peer, uint sessionId,
        IReadOnlyList<InputEvent> events, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await peer.SendAsync(new ProtocolMessage(MessageKind.StreamData, 0, sessionId,
            "input.event", InputWire.Encode(events)), timeout.Token).ConfigureAwait(false);
    }

    private static async Task ObserveCancelledAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
