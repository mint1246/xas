using Xas.Core;
using Xas.Input;

namespace Xas.Tests;

/// <summary>Checks that compositor metadata updates rebind input without churning the virtual panel.</summary>
public static class HandoffMetadataTests
{
    private static readonly DisplayMetadata Initial = new()
    {
        Id = "test-display", Name = "Test display", WidthPixels = 1280, HeightPixels = 720,
        NativeWidthPixels = 2560, NativeHeightPixels = 1440,
        LogicalX = -1280, LogicalY = 0, LogicalWidth = 1280, LogicalHeight = 720,
        RefreshMilliHertz = 60000, PhysicalWidthMillimeters = 600, PhysicalHeightMillimeters = 340,
        Scale = 2
    };

    private static readonly VirtualDisplayAttachment Attachment = new(
        @"\\.\DISPLAY2", @"MONITOR\TEST", Guid.NewGuid(), 2560, 1440, 60);

    public static async Task RunAsync()
    {
        await MetadataChangesOnlyRebindInputAsync();
        await NativeModeChangeRecreatesMonitorAsync();
        await RefreshChangeRecreatesMonitorAsync();
    }

    private static async Task MetadataChangesOnlyRebindInputAsync()
    {
        var updates = new DisplayMetadata[]
        {
            Initial with { Name = "Renamed panel" },
            Initial with { LogicalX = -2560, LogicalY = 80 },
            Initial with { Scale = 1.5 },
            Initial with { PhysicalWidthMillimeters = 610, PhysicalHeightMillimeters = 350 }
        };
        await RunChangeAsync(updates, expectedAttachCalls: 1);
    }

    private static Task NativeModeChangeRecreatesMonitorAsync() =>
        RunChangeAsync([Initial with { NativeWidthPixels = 3840 }], expectedAttachCalls: 2);

    private static Task RefreshChangeRecreatesMonitorAsync() =>
        RunChangeAsync([Initial with { RefreshMilliHertz = 75000 }], expectedAttachCalls: 2);

    private static async Task RunChangeAsync(DisplayMetadata[] updates, int expectedAttachCalls)
    {
        using var stop = new CancellationTokenSource();
        var peer = new MutableFakePeer(Initial);
        var controller = new FakeController();
        var routerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routerStarts = 0;
        var run = WindowsMonitorHandoff.RunCoreAsync(peer, controller,
            (_, _, token, _) =>
            {
                Interlocked.Increment(ref routerStarts);
                routerStarted.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            }, _ => new WindowsMonitor(
                Attachment.DeviceName, "Test virtual monitor", Attachment.MonitorHardwareId,
                new WindowsCaptureRegion(2560, 0, 5120, 1440)),
            _ => { }, _ => true,
            (_, token) => Task.Delay(Timeout.Infinite, token), stop.Token);

        try
        {
            await routerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var expectedRouterStarts = 1;
            foreach (var update in updates)
            {
                var previous = routerStarted;
                routerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                peer.SetDisplay(update);
                expectedRouterStarts++;
                await routerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (previous.Task.IsFaulted) throw new InvalidOperationException("A prior router start failed.");
            }

            if (routerStarts != expectedRouterStarts)
                throw new InvalidOperationException($"Expected {expectedRouterStarts} router bindings, got {routerStarts}.");
            if (controller.AttachCalls != expectedAttachCalls)
                throw new InvalidOperationException($"Expected {expectedAttachCalls} monitor attachments, got {controller.AttachCalls}.");
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class FakeController : IVirtualDisplayController
    {
        public bool IsAvailable => true;
        public int AttachCalls { get; private set; }
        public ValueTask<VirtualDisplayAttachment> AttachAsync(DisplayMetadata display, CancellationToken token)
        {
            AttachCalls++;
            return ValueTask.FromResult(Attachment);
        }
        public ValueTask DetachAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableFakePeer(DisplayMetadata display) : IHotInputPeer
    {
        public DisplayMetadata? Display { get; private set; } = display;
        public bool RealtimeReady => true;
        public event Action? Changed;
        public event Action? Disconnected { add { } remove { } }

        public void SetDisplay(DisplayMetadata? display)
        {
            Display = display;
            Changed?.Invoke();
        }

        public ValueTask<ProtocolMessage> RequestRealtimeAsync(string method, byte[] payload,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SendRealtimeAsync(ProtocolMessage message,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
