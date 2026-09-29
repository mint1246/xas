using Xas.Core;
using Xas.Input;

namespace Xas.Tests;

/// <summary>Exercises handoff attachment lifetime without touching Windows display or input APIs.</summary>
public static class HandoffLifecycleTests
{
    private static readonly DisplayMetadata Display = new()
    { Id = "test-display", Name = "Test display", WidthPixels = 1920, HeightPixels = 1080 };
    private static readonly VirtualDisplayAttachment Attachment = new(
        @"\\.\DISPLAY2", @"MONITOR\TEST", Guid.NewGuid(), 1920, 1080, 60);
    private static readonly WindowsMonitor Monitor = new(
        Attachment.DeviceName, "Test virtual monitor", Attachment.MonitorHardwareId,
        new WindowsCaptureRegion(1920, 0, 3840, 1080));

    public static async Task RunAsync()
    {
        await InputFailuresKeepAttachmentAsync();
        await CancellationDisposesOnceAndObservesWaitAsync();
        await RemovedMonitorIsRecreatedAsync();
    }

    private static async Task InputFailuresKeepAttachmentAsync()
    {
        using var stop = new CancellationTokenSource();
        var peer = new FakePeer(Display);
        var controller = new FakeController();
        var starts = 0;
        var delays = 0;
        await WindowsMonitorHandoff.RunCoreAsync(peer, controller,
            (_, _, _, _) =>
            {
                starts++;
                if (starts <= 2) throw new IOException("synchronous simulated input.open failure");
                if (starts <= 4) return Task.FromException(new IOException("asynchronous simulated router failure"));
                return Task.Delay(Timeout.Infinite, stop.Token);
            }, _ => Monitor, _ => { }, _ => true,
            (_, token) =>
            {
                if (++delays == 4) stop.Cancel();
                return Task.CompletedTask;
            }, stop.Token);

        Assert(starts == 4, "Router failures did not retry the input lease.");
        Assert(controller.AttachCalls == 1, "An input failure detached or recreated the virtual display.");
        Assert(controller.DetachCalls == 0 && controller.DisposeCalls == 1,
            "Cancellation must leave final monitor cleanup to one controller disposal.");
        Assert(peer.ChangeSubscribers == 0, "The peer change handler remained subscribed after cancellation.");
    }

    private static async Task CancellationDisposesOnceAndObservesWaitAsync()
    {
        using var stop = new CancellationTokenSource();
        var peer = new FakePeer(Display);
        var controller = new FakeController();
        var routerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = WindowsMonitorHandoff.RunCoreAsync(peer, controller,
            (_, _, token, _) =>
            {
                routerStarted.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            }, _ => Monitor, _ => { }, _ => true,
            (_, token) => Task.Delay(Timeout.Infinite, token), stop.Token);
        await routerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert(controller.AttachCalls == 1 && controller.DetachCalls == 0 && controller.DisposeCalls == 1,
            "Cancellation must dispose the owning controller exactly once.");
        Assert(peer.ChangeSubscribers == 0, "Cancellation left a peer event subscription behind.");
    }

    private static async Task RemovedMonitorIsRecreatedAsync()
    {
        using var stop = new CancellationTokenSource();
        var peer = new FakePeer(Display);
        var controller = new FakeController();
        var routerStarts = 0;
        var secondRouter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = WindowsMonitorHandoff.RunCoreAsync(peer, controller,
            (_, _, token, topologyChanged) =>
            {
                if (++routerStarts == 1)
                {
                    topologyChanged();
                    return Task.Delay(Timeout.Infinite, token);
                }
                secondRouter.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            }, _ => Monitor, _ => { }, _ => false,
            (_, token) => Task.Delay(Timeout.Infinite, token), stop.Token);
        await secondRouter.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert(controller.AttachCalls == 2,
            "A monitor confirmed missing from Windows topology was not recreated.");
        Assert(controller.DetachCalls == 0 && controller.DisposeCalls == 1,
            "Removal recovery should remain owned by the controller until shutdown.");
        Assert(routerStarts == 2, "The monitor removal did not rebind input after recreation.");
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FakeController : IVirtualDisplayController
    {
        public bool IsAvailable => true;
        public int AttachCalls { get; private set; }
        public int DetachCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public ValueTask<VirtualDisplayAttachment> AttachAsync(DisplayMetadata display, CancellationToken token)
        { AttachCalls++; return ValueTask.FromResult(Attachment); }
        public ValueTask DetachAsync(CancellationToken token) { DetachCalls++; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }

    private sealed class FakePeer(DisplayMetadata display) : IHotInputPeer
    {
        public DisplayMetadata? Display { get; } = display;
        public bool RealtimeReady => true;
        public event Action? Changed;
        public event Action? Disconnected { add { } remove { } }
        public int ChangeSubscribers => Changed?.GetInvocationList().Length ?? 0;
        public ValueTask<ProtocolMessage> RequestRealtimeAsync(string method, byte[] payload,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SendRealtimeAsync(ProtocolMessage message,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
