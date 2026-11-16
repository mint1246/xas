using System.Threading.Channels;
using System.Diagnostics;
using Xas.Core;
using Xas.Input.Display;

namespace Xas.Input;

/// <summary>Owns the XAS virtual monitor and routes native input over an already authenticated peer session.</summary>
public static class WindowsMonitorHandoff
{
    private readonly record struct RouterOutcome(bool Refresh, bool RetryRouter, bool TopologyChanged);

    public static async Task RunAsync(IHotInputPeer peer, CancellationToken token, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (!OperatingSystem.IsWindows()) return;
        // XAS is launched into the logged-in user's desktop by the background Windows service. In that
        // arrangement Environment.UserInteractive is not a reliable indicator: the process may inherit
        // service-style launch characteristics even though it is genuinely running in the interactive
        // user's session. Session 0 is the actual boundary that cannot own desktop input/display state.
        if (Process.GetCurrentProcess().SessionId == 0)
        {
            log?.Invoke("KVM handoff is unavailable from Windows Session 0.");
            return;
        }

        var controller = new SudoVdaDisplayController(log);
        if (!controller.IsAvailable)
        {
            log?.Invoke("No virtual display driver is installed; install SudoVDA to enable automatic handoff.");
            await controller.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await RunCoreAsync(peer, controller,
            (hotPeer, region, cancellationToken, topologyChanged) =>
                HotInputRouter.RunAsync(hotPeer, region, cancellationToken, topologyChanged), FindOwnedRemote,
            monitor => _ = WindowsMonitorTopology.ReturnToLocal(monitor),
            attachment => WindowsMonitorTopology.Exists(attachment.DeviceName, attachment.MonitorHardwareId),
            static (duration, cancellationToken) => Task.Delay(duration, cancellationToken), token, log)
            .ConfigureAwait(false);
    }

    internal static async Task RunCoreAsync(IHotInputPeer peer, IVirtualDisplayController controller,
        Func<IHotInputPeer, WindowsCaptureRegion, CancellationToken, Action, Task> runRouter,
        Func<VirtualDisplayAttachment, WindowsMonitor?> findMonitor,
        Action<WindowsMonitor> returnToLocal,
        Func<VirtualDisplayAttachment, bool> attachmentExists,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken token, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(runRouter);
        ArgumentNullException.ThrowIfNull(findMonitor);
        ArgumentNullException.ThrowIfNull(returnToLocal);
        ArgumentNullException.ThrowIfNull(attachmentExists);
        ArgumentNullException.ThrowIfNull(delay);
        await using var ownedController = controller;
        if (!controller.IsAvailable) return;
        var changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
        void Changed() => changes.Writer.TryWrite(0);
        void TopologyChanged() => changes.Writer.TryWrite(0);
        peer.Changed += Changed;
        try
        {
            await RunHandoffLoopAsync(peer, controller, runRouter, findMonitor, returnToLocal,
                attachmentExists, delay, changes.Reader, token, log, TopologyChanged)
                .ConfigureAwait(false);
        }
        finally
        {
            peer.Changed -= Changed;
            changes.Writer.TryComplete();
        }
    }

    private static async Task RunHandoffLoopAsync(
        IHotInputPeer peer, IVirtualDisplayController controller,
        Func<IHotInputPeer, WindowsCaptureRegion, CancellationToken, Action, Task> runRouter,
        Func<VirtualDisplayAttachment, WindowsMonitor?> findMonitor,
        Action<WindowsMonitor> returnToLocal,
        Func<VirtualDisplayAttachment, bool> attachmentExists,
        Func<TimeSpan, CancellationToken, Task> delay,
        ChannelReader<byte> changes, CancellationToken token, Action<string>? log,
        Action topologySignal)
    {
        DisplayMetadata? attachedDisplay = null;
        VirtualDisplayAttachment? attachment = null;
        while (!token.IsCancellationRequested)
        {
            var display = peer.Display;
            if (display is null)
            {
                await WaitForChangeAsync(changes, token).ConfigureAwait(false);
                continue;
            }

            if (!HasSameVirtualMonitorMode(display, attachedDisplay) || attachment is null ||
                !attachmentExists(attachment))
            {
                log?.Invoke(
                    $"Attaching virtual display for {display.Name} at " +
                    $"{display.WidthPixels}x{display.HeightPixels}.");
                attachment = await controller.AttachAsync(display, token).ConfigureAwait(false);
                attachedDisplay = display;
            }

            var monitor = await FindMonitorOrWaitAsync(attachment!, findMonitor, delay, token, log)
                .ConfigureAwait(false);
            if (monitor is null) continue;

            var outcome = await RunRouterUntilChangeAsync(
                peer, monitor, attachedDisplay, runRouter, changes, returnToLocal,
                log, token, topologySignal).ConfigureAwait(false);
            if (token.IsCancellationRequested) break;
            if (outcome.RetryRouter)
            {
                await delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                continue;
            }

            if (outcome.Refresh)
                (attachment, attachedDisplay) = await RefreshAttachmentAsync(
                    peer.Display, attachedDisplay, attachment, controller, outcome.TopologyChanged,
                    token, log).ConfigureAwait(false);
        }
    }

    private static async Task<WindowsMonitor?> FindMonitorOrWaitAsync(
        VirtualDisplayAttachment attachment,
        Func<VirtualDisplayAttachment, WindowsMonitor?> findMonitor,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken token, Action<string>? log)
    {
        WindowsMonitor? monitor;
        try
        {
            monitor = findMonitor(attachment);
        }
        catch (Exception exception) when (IsTransientTopologyFailure(exception))
        {
            log?.Invoke($"Windows display topology is temporarily unavailable: {exception.Message}");
            await delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            return null;
        }

        if (monitor is not null) return monitor;

        log?.Invoke("The owned virtual display is not available in the Windows desktop topology.");
        await delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        return null;
    }

    private static async Task<(VirtualDisplayAttachment? Attachment, DisplayMetadata? Display)>
        RefreshAttachmentAsync(
            DisplayMetadata? updatedDisplay, DisplayMetadata? attachedDisplay,
            VirtualDisplayAttachment? priorAttachment, IVirtualDisplayController controller,
            bool topologyChanged, CancellationToken token, Action<string>? log)
    {
        var metadataChanged = !Equals(updatedDisplay, attachedDisplay);
        var modeChanged = updatedDisplay is not null &&
            !HasSameVirtualMonitorMode(updatedDisplay, attachedDisplay);

        if (!metadataChanged && topologyChanged)
        {
            // Moving monitors in Windows only rebinds the capture rectangle, preserving the user's layout.
            log?.Invoke("Windows display topology changed; rebinding input to the existing virtual monitor.");
            return (priorAttachment, attachedDisplay);
        }

        if (updatedDisplay is null)
        {
            log?.Invoke("Remote display disappeared; removing the virtual monitor.");
            await controller.DetachAsync(token).ConfigureAwait(false);
            return (null, null);
        }

        if (modeChanged)
        {
            log?.Invoke("Remote display mode changed; refreshing the virtual monitor.");
            var refreshedAttachment = await controller.AttachAsync(updatedDisplay, token).ConfigureAwait(false);
            return (refreshedAttachment, updatedDisplay);
        }

        return (priorAttachment, metadataChanged ? updatedDisplay : attachedDisplay);
    }

    private static async Task<RouterOutcome> RunRouterUntilChangeAsync(
        IHotInputPeer peer, WindowsMonitor monitor, DisplayMetadata? attachedDisplay,
        Func<IHotInputPeer, WindowsCaptureRegion, CancellationToken, Action, Task> runRouter,
        ChannelReader<byte> changes, Action<WindowsMonitor> returnToLocal,
        Action<string>? log, CancellationToken token, Action topologySignal)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(token);
        var topologyChanged = false;
        void MarkTopologyChanged()
        {
            topologyChanged = true;
            topologySignal();
        }

        var router = StartRouter(runRouter, peer, monitor.Region, run.Token, MarkTopologyChanged);
        var refresh = false;
        var retryRouter = false;
        Task<bool>? pendingChange = null;
        while (!token.IsCancellationRequested && !refresh)
        {
            pendingChange ??= changes.WaitToReadAsync(run.Token).AsTask();
            var completed = await Task.WhenAny(router, pendingChange).ConfigureAwait(false);
            if (completed == router)
            {
                retryRouter = await ObserveRouterAsync(router, run, monitor, returnToLocal, log)
                    .ConfigureAwait(false);
                break;
            }

            if (!await ObserveChangeAsync(pendingChange, run).ConfigureAwait(false)) break;
            pendingChange = null;
            while (changes.TryRead(out _)) { }
            refresh = topologyChanged || !Equals(peer.Display, attachedDisplay);
        }

        run.Cancel();
        if (pendingChange is not null)
            await ObservePendingChangeAsync(pendingChange, run).ConfigureAwait(false);
        await ObserveRouterCleanupAsync(router, run, retryRouter).ConfigureAwait(false);
        return new(refresh, retryRouter, topologyChanged);
    }

    private static async Task<bool> ObserveRouterAsync(
        Task router, CancellationTokenSource run, WindowsMonitor monitor,
        Action<WindowsMonitor> returnToLocal, Action<string>? log)
    {
        var retryRouter = false;
        try
        {
            await router.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        catch (Exception exception)
        {
            log?.Invoke(
                $"Remote input is temporarily unavailable; " +
                $"keeping the virtual display attached: {exception.Message}");
            retryRouter = true;
        }

        if (!retryRouter && !run.IsCancellationRequested)
        {
            returnToLocal(monitor);
            log?.Invoke("Input returned to the local desktop.");
        }

        return retryRouter;
    }

    private static async Task<bool> ObserveChangeAsync(
        Task<bool> pendingChange, CancellationTokenSource run)
    {
        try
        {
            await pendingChange.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            // The linked token cancels this pending topology wait during router cleanup.
            return false;
        }
    }

    private static async Task ObservePendingChangeAsync(
        Task<bool> pendingChange, CancellationTokenSource run)
    {
        try { await pendingChange.ConfigureAwait(false); }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
    }

    private static async Task ObserveRouterCleanupAsync(
        Task router, CancellationTokenSource run, bool retryRouter)
    {
        try { await router.ConfigureAwait(false); }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        catch (Exception) when (retryRouter) { }
    }

    private static WindowsMonitor? FindOwnedRemote(VirtualDisplayAttachment attachment)
    {
        var monitors = WindowsMonitorTopology.Enumerate();
        var owned = monitors.FirstOrDefault(m => WindowsMonitorTopology.MatchesIdentity(
            m, attachment.DeviceName, attachment.MonitorHardwareId));
        return owned is null ? null : WindowsMonitorTopology.FindRemote(monitors, owned.DeviceName);
    }

    private static bool IsTransientTopologyFailure(Exception exception) =>
        exception is InvalidOperationException or System.ComponentModel.Win32Exception;

    private static bool HasSameVirtualMonitorMode(DisplayMetadata current, DisplayMetadata? attached)
    {
        if (attached is null || !string.Equals(current.Id, attached.Id, StringComparison.Ordinal)) return false;
        return SudoVdaDisplayController.ResolveVirtualWidth(current) ==
                   SudoVdaDisplayController.ResolveVirtualWidth(attached) &&
               SudoVdaDisplayController.ResolveVirtualHeight(current) ==
                   SudoVdaDisplayController.ResolveVirtualHeight(attached) &&
               SudoVdaDisplayController.ResolveRefreshHertz(current.RefreshMilliHertz) ==
                   SudoVdaDisplayController.ResolveRefreshHertz(attached.RefreshMilliHertz);
    }

    private static Task StartRouter(
        Func<IHotInputPeer, WindowsCaptureRegion, CancellationToken, Action, Task> runRouter,
        IHotInputPeer peer, WindowsCaptureRegion region, CancellationToken token, Action topologyChanged)
    {
        try { return runRouter(peer, region, token, topologyChanged); }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    private static async Task WaitForChangeAsync(ChannelReader<byte> changes, CancellationToken token)
    {
        await changes.WaitToReadAsync(token).ConfigureAwait(false);
        while (changes.TryRead(out _)) { }
    }

}
