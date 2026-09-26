using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Transport;

namespace Xas.Input;

/// <summary>Manual Windows input takeover over one authenticated connection.</summary>
public static class InputControlClient
{
    public static async Task RunAsync(ConfiguredPeer configured, DeviceIdentity identity,
        PeerTrustStore trust, CancellationToken cancellationToken, WindowsCaptureRegion? region = null,
        string? monitorHint = null, Action<string>? trace = null,
        IInputPipelineMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(configured);
        if (!WindowsInputCapture.IsAvailable)
            throw new PlatformNotSupportedException("Manual input capture requires a Windows user session.");
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (region is not null) setup.CancelAfter(TimeSpan.FromSeconds(55));
        await using var connection = await MutualTlsTransport.ConnectAsync(configured.Host, configured.Port,
            identity, trust, configured.DeviceId, region is null ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3),
            setup.Token).ConfigureAwait(false);
        await using var frames = new BinaryFrameConnection(connection.Stream, leaveOpen: true);
        await using var peer = new MultiplexedProtocolPeer(frames, (_, _) =>
            ValueTask.FromException<ProtocolMessage>(new NotSupportedException("The input controller does not accept remote requests.")));
        var infoReply = await peer.RequestAsync("device.info", [], cancellationToken: setup.Token).ConfigureAwait(false);
        DeviceInfo? info;
        try { info = JsonSerializer.Deserialize<DeviceInfo>(infoReply.Payload); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid device.info response.", ex); }
        if (info?.DeviceId != configured.DeviceId) throw new InvalidDataException("Device identity changed during input setup.");
        if (!info.Capabilities.Any(c => c.Capability == Capability.Input && c.Version >= 1))
            throw new NotSupportedException("The remote device has no available input injection backend.");
        DisplayMetadata? display = null;
        if (region is not null)
        {
            if (!info.Capabilities.Any(c => c.Capability == Capability.Input && c.Version >= 2) ||
                !info.Capabilities.Any(c => c.Capability == Capability.Display && c.Version >= 1))
                throw new NotSupportedException("Automatic monitor handoff requires remote display metadata and absolute input positioning.");
            var displayReply = await peer.RequestAsync("display.info", [], cancellationToken: setup.Token).ConfigureAwait(false);
            display = JsonSerializer.Deserialize<DisplayMetadata>(displayReply.Payload)
                ?? throw new InvalidDataException("The remote display metadata was empty.");
            if (display.WidthPixels is < 1 or > 16384 || display.HeightPixels is < 1 or > 16384)
                throw new InvalidDataException("The remote display dimensions are invalid.");
        }

        var opened = await peer.RequestAsync("input.open", [], cancellationToken: setup.Token).ConfigureAwait(false);
        if (opened.Payload.Length != 4) throw new InvalidDataException("Invalid input.open response.");
        var sessionId = BinaryPrimitives.ReadUInt32BigEndian(opened.Payload);
        if (sessionId == 0) throw new InvalidDataException("The remote input session ID was zero.");

        var sent = 0;
        try
        {
            if (region is { } bounds && display is not null)
            {
                if (!WindowsMonitorTopology.TryGetPointer(out var x, out var y) || !bounds.Contains(x, y))
                    throw new InvalidOperationException("Pointer left the virtual monitor during remote setup.");
                var (targetX, targetY) = bounds.MapToRemote(x, y,
                    display.WidthPixels, display.HeightPixels);
                await SendWithTimeoutAsync(peer, sessionId,
                    [new CapturedInput(new InputEvent(InputEventKind.MoveAbsolute, X: targetX, Y: targetY), 0, 0)],
                    setup.Token).ConfigureAwait(false);
            }
            if (region is null) Console.Error.WriteLine("Remote input is active. Press Ctrl+Alt+Esc to return control locally.");
            await using var capture = await WindowsInputCapture.StartAsync(cancellationToken, region,
                display?.WidthPixels ?? 0, display?.HeightPixels ?? 0, metrics).ConfigureAwait(false);
            using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sendTask = SendEventsAsync(peer, capture, sessionId, active.Token, () => sent++, metrics);
            var heartbeatTask = SendHeartbeatAsync(peer, sessionId, active.Token);
            var boundaryTask = region is null ? Task.Delay(Timeout.Infinite, active.Token) :
                WatchBoundaryAsync(capture, region.Value, monitorHint, active.Token);
            var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
            var first = await Task.WhenAny(capture.Completion, sendTask, heartbeatTask,
                boundaryTask, peer.Completion, cancelled).ConfigureAwait(false);
            active.Cancel();
            try { await capture.DisposeAsync().ConfigureAwait(false); }
            catch when (first != capture.Completion) { /* Observe the primary send/connection failure below. */ }
            if (first == cancelled) cancellationToken.ThrowIfCancellationRequested();
            if (first == peer.Completion) throw new IOException("The remote input connection closed.");
            // Name the task that ended the session: the boundary watcher, the event pump, and the heartbeat
            // all race here, and which one wins is the whole diagnosis when a session will not stay open.
            trace?.Invoke($"input session ended by {Name(first)} after {sent} event(s)");

            string Name(Task task) => ReferenceEquals(task, capture.Completion) ? "capture"
                : ReferenceEquals(task, sendTask) ? "event-pump"
                : ReferenceEquals(task, heartbeatTask) ? "heartbeat"
                : ReferenceEquals(task, boundaryTask) ? "boundary-watch"
                : ReferenceEquals(task, peer.Completion) ? "peer" : "cancelled";
            if (first == sendTask) await sendTask.ConfigureAwait(false);
            if (first == heartbeatTask) await heartbeatTask.ConfigureAwait(false);
            await capture.Completion.ConfigureAwait(false);
            await ObserveCancelledAsync(sendTask).ConfigureAwait(false);
            await ObserveCancelledAsync(heartbeatTask).ConfigureAwait(false);
            await ObserveCancelledAsync(boundaryTask).ConfigureAwait(false);
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
        uint sessionId, CancellationToken token, Action onSent, IInputPipelineMetrics? metrics)
    {
        var batch = new List<CapturedInput>(InputWire.MaxEventsPerFrame);
        await foreach (var input in capture.Events.ReadAllAsync(token).ConfigureAwait(false))
        {
            batch.Add(input);
            while (batch.Count < InputWire.MaxEventsPerFrame && capture.Events.TryRead(out var next))
                batch.Add(next);
            await SendWithTimeoutAsync(peer, sessionId, batch, token).ConfigureAwait(false);
            if (metrics is not null) metrics.Record(new(InputPipelineStage.Send, Stopwatch.GetTimestamp(), batch.Count));
            onSent();
            batch.Clear();
        }
    }

    private static async Task SendHeartbeatAsync(MultiplexedProtocolPeer peer, uint sessionId,
        CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            await SendWithTimeoutAsync(peer, sessionId,
                [new CapturedInput(new InputEvent(InputEventKind.KeepAlive), 0, 0)], token)
                .ConfigureAwait(false);
        }
    }

    private static async Task SendWithTimeoutAsync(MultiplexedProtocolPeer peer, uint sessionId,
        IReadOnlyList<CapturedInput> events, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await peer.SendAsync(new ProtocolMessage(MessageKind.StreamData, 0, sessionId,
            "input.event", InputWire.Encode(events.Select(item => item.Event).ToArray())), timeout.Token).ConfigureAwait(false);
    }

    private static async Task ObserveCancelledAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static async Task WatchBoundaryAsync(WindowsInputCapture capture,
        WindowsCaptureRegion region, string? monitorHint, CancellationToken token)
    {
        var ticks = 0;
        while (true)
        {
            await Task.Delay(25, token).ConfigureAwait(false);
            // The hint must be the same pinned monitor the caller handed off on. Resolving the target
            // afresh would be ambiguous whenever another virtual monitor shares the adapter, and the
            // session would end itself within half a second of opening.
            if (!WindowsMonitorTopology.TryGetPointer(out var x, out var y) || !region.Contains(x, y) ||
                (++ticks % 20 == 0 && WindowsMonitorTopology.FindRemote(monitorHint)?.Region != region))
            { capture.EndCapture(); return; }
        }
    }
}
