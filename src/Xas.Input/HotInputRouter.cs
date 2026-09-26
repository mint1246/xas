using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using Xas.Core;
using Xas.Core.Protocol;

namespace Xas.Input;

/// <summary>Routes native Windows input to a ready peer only while the pointer is on its virtual monitor.</summary>
public static class HotInputRouter
{
    public static async Task RunAsync(IHotInputPeer peer, WindowsCaptureRegion region,
        CancellationToken cancellationToken, Action? displayTopologyChanged = null,
        IInputPipelineMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var display = peer.Display ?? throw new InvalidOperationException("The peer has no display metadata.");
        if (display.WidthPixels is < 1 or > 16384 || display.HeightPixels is < 1 or > 16384)
            throw new InvalidDataException("The peer display dimensions are invalid.");

        await using var capture = await WindowsInputCapture.StartHotAsync(region,
            display.WidthPixels, display.HeightPixels, cancellationToken, metrics).ConfigureAwait(false);
        var wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
        var disconnected = false;
        var emergencyRequested = false;
        void Signal() => wake.Writer.TryWrite(0);
        void SignalPointer(bool _) => Signal();
        void SignalRouting(bool _) => Signal();
        void Changed() => Signal();
        void Disconnected() { disconnected = true; Signal(); }
        void Emergency() { emergencyRequested = true; Signal(); }
        void TopologyChanged() { displayTopologyChanged?.Invoke(); Signal(); }
        capture.PointerRegionChanged += SignalPointer;
        capture.RoutingChanged += SignalRouting;
        capture.EmergencyReturnRequested += Emergency;
        capture.DisplayTopologyChanged += TopologyChanged;
        peer.Changed += Changed;
        peer.Disconnected += Disconnected;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !disconnected)
            {
                if (emergencyRequested) return;
                if (peer.RealtimeReady)
                {
                    await RunLeaseAsync(peer, capture, wake.Reader, Signal, () => disconnected,
                        () => emergencyRequested, metrics, cancellationToken).ConfigureAwait(false);
                    if (emergencyRequested) return;
                }
                if (capture.Completion.IsCompleted) await capture.Completion.ConfigureAwait(false);
                await wake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                while (wake.Reader.TryRead(out _)) { }
            }
        }
        finally
        {
            capture.PointerRegionChanged -= SignalPointer;
            capture.RoutingChanged -= SignalRouting;
            capture.EmergencyReturnRequested -= Emergency;
            capture.DisplayTopologyChanged -= TopologyChanged;
            peer.Changed -= Changed;
            peer.Disconnected -= Disconnected;
            capture.SetRoutingEnabled(false);
        }
    }

    private static async Task RunLeaseAsync(IHotInputPeer peer, WindowsInputCapture capture,
        ChannelReader<byte> wake, Action signalWake, Func<bool> isDisconnected, Func<bool> isEmergency,
        IInputPipelineMetrics? metrics, CancellationToken cancellationToken)
    {
        var opened = await peer.RequestRealtimeAsync("input.open", [], cancellationToken).ConfigureAwait(false);
        if (opened.Payload.Length != 4) throw new InvalidDataException("Invalid input.open response.");
        var sessionId = BinaryPrimitives.ReadUInt32BigEndian(opened.Payload);
        if (sessionId == 0) throw new InvalidDataException("The remote input session ID was zero.");

        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var sendGate = new SemaphoreSlim(1, 1);
        var send = SendEventsAsync(peer, capture, sessionId, sendGate, active.Token, metrics);
        var heartbeat = SendHeartbeatAsync(peer, sessionId, sendGate, active.Token, metrics);
        var routeChanged = false;
        capture.RoutingChanged += OnRoutingChanged;
        capture.SetRoutingEnabled(true);
        routeChanged = capture.IsRoutingRemote;
        try
        {
            while (!active.IsCancellationRequested)
            {
                var signal = wake.WaitToReadAsync(active.Token).AsTask();
                var finished = await Task.WhenAny(signal, send, heartbeat, capture.Completion).ConfigureAwait(false);
                if (finished == send) await send.ConfigureAwait(false);
                if (finished == heartbeat) await heartbeat.ConfigureAwait(false);
                if (finished == capture.Completion) await capture.Completion.ConfigureAwait(false);
                if (finished != signal) continue;
                await signal.ConfigureAwait(false);
                while (wake.TryRead(out _)) { }

                var routing = capture.IsRoutingRemote;
                if (routeChanged && !routing && peer.RealtimeReady)
                    await SendReleaseAsync(peer, sessionId, sendGate, active.Token).ConfigureAwait(false);
                routeChanged = routing;
                if (isDisconnected() || !peer.RealtimeReady || isEmergency()) return;
            }
        }
        finally
        {
            capture.RoutingChanged -= OnRoutingChanged;
            if (capture.IsRoutingRemote && peer.RealtimeReady)
            {
                try { await SendReleaseAsync(peer, sessionId, sendGate, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
            }
            capture.SetRoutingEnabled(false);
            active.Cancel();
            await ObserveAsync(send).ConfigureAwait(false);
            await ObserveAsync(heartbeat).ConfigureAwait(false);
            if (!isDisconnected() && peer.RealtimeReady)
            {
                var payload = SessionPayload(sessionId);
                try
                {
                    await sendGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await peer.RequestRealtimeAsync("input.close", payload, CancellationToken.None)
                            .AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    finally { sendGate.Release(); }
                }
                catch (Exception) { /* Disconnect and timeout both release the server-side lease. */ }
            }
        }

        void OnRoutingChanged(bool _) => signalWake();
    }

    private static async Task SendReleaseAsync(IHotInputPeer peer, uint sessionId, SemaphoreSlim sendGate,
        CancellationToken token)
    {
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await peer.RequestRealtimeAsync("input.release", SessionPayload(sessionId), token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }
        finally { sendGate.Release(); }
    }

    private static byte[] SessionPayload(uint sessionId)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, sessionId);
        return payload;
    }

    private static async Task SendEventsAsync(IHotInputPeer peer, WindowsInputCapture capture,
        uint sessionId, SemaphoreSlim sendGate, CancellationToken token, IInputPipelineMetrics? metrics)
    {
        var batch = new List<CapturedInput>(InputWire.MaxEventsPerFrame);
        await foreach (var input in capture.Events.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (!capture.IsRoutingRemote) continue;
            batch.Add(input);
            while (batch.Count < InputWire.MaxEventsPerFrame && capture.Events.TryRead(out var next))
                if (capture.IsRoutingRemote) batch.Add(next);
            if (batch.Count == 0) continue;
            await sendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (capture.IsRoutingRemote)
                {
                    var events = batch.Select(item => item.Event).ToArray();
                    await peer.SendRealtimeAsync(new ProtocolMessage(MessageKind.StreamData, 0, sessionId,
                        "input.event", InputWire.Encode(events)), token).ConfigureAwait(false);
                    if (metrics is not null)
                        metrics.Record(new(InputPipelineStage.Send, Stopwatch.GetTimestamp(), batch.Count));
                }
            }
            finally { sendGate.Release(); }
            batch.Clear();
        }
    }

    private static async Task SendHeartbeatAsync(IHotInputPeer peer, uint sessionId,
        SemaphoreSlim sendGate, CancellationToken token, IInputPipelineMetrics? metrics)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            await sendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await peer.SendRealtimeAsync(new ProtocolMessage(MessageKind.StreamData, 0, sessionId,
                    "input.event", InputWire.Encode([new InputEvent(InputEventKind.KeepAlive)])), token)
                    .ConfigureAwait(false);
                if (metrics is not null)
                    metrics.Record(new(InputPipelineStage.Send, Stopwatch.GetTimestamp(), 1));
            }
            finally { sendGate.Release(); }
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
