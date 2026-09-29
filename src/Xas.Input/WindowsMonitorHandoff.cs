using System.Threading.Channels;
using System.Diagnostics;
using Xas.Core;
using Xas.Input.Display;

namespace Xas.Input;

/// <summary>Owns the XAS virtual monitor and routes native input over an already authenticated peer session.</summary>
public static class WindowsMonitorHandoff
{
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
        var topologyChanged = false;
        void Changed() => changes.Writer.TryWrite(0);
        void TopologyChanged() { topologyChanged = true; changes.Writer.TryWrite(0); }
        peer.Changed += Changed;
        DisplayMetadata? attachedDisplay = null;
        VirtualDisplayAttachment? attachment = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var display = peer.Display;
                if (display is null)
                {
                    await WaitForChangeAsync(changes.Reader, token).ConfigureAwait(false);
                    continue;
                }

                if (!Equals(display, attachedDisplay) || attachment is null ||
                    !attachmentExists(attachment))
                {
                    log?.Invoke($"Attaching virtual display for {display.Name} at {display.WidthPixels}x{display.HeightPixels}.");
                    attachment = await controller.AttachAsync(display, token).ConfigureAwait(false);
                    attachedDisplay = display;
                }

                WindowsMonitor? monitor;
                try { monitor = findMonitor(attachment); }
                catch (Exception ex) when (IsTransientTopologyFailure(ex))
                {
                    log?.Invoke($"Windows display topology is temporarily unavailable: {ex.Message}");
                    await delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    continue;
                }
                if (monitor is null)
                {
                    log?.Invoke("The owned virtual display is not available in the Windows desktop topology.");
                    await delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    continue;
                }

                using var run = CancellationTokenSource.CreateLinkedTokenSource(token);
                topologyChanged = false;
                var router = StartRouter(runRouter, peer, monitor.Region, run.Token, TopologyChanged);
                var refresh = false;
                var retryRouter = false;
                Task<bool>? pendingChange = null;
                while (!token.IsCancellationRequested && !refresh)
                {
                    pendingChange ??= changes.Reader.WaitToReadAsync(run.Token).AsTask();
                    var completed = await Task.WhenAny(router, pendingChange).ConfigureAwait(false);
                    if (completed == router)
                    {
                        try { await router.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
                        catch (Exception ex)
                        {
                            // Input acquisition can fail independently of display metadata. Keep the display
                            // attached and retry the input lease without bouncing the user's monitor.
                            log?.Invoke($"Remote input is temporarily unavailable; keeping the virtual display attached: {ex.Message}");
                            retryRouter = true;
                        }
                        // The hot router only completes normally for the emergency return or peer disconnect.
                        if (!retryRouter && !token.IsCancellationRequested)
                        {
                            returnToLocal(monitor);
                            log?.Invoke("Input returned to the local desktop.");
                        }
                        break;
                    }
                    try { await pendingChange.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (run.IsCancellationRequested) { break; }
                    pendingChange = null;
                    while (changes.Reader.TryRead(out _)) { }
                    if (topologyChanged || !Equals(peer.Display, attachedDisplay)) refresh = true;
                }
                run.Cancel();
                if (pendingChange is not null)
                {
                    try { await pendingChange.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (run.IsCancellationRequested) { }
                }
                try { await router.ConfigureAwait(false); }
                catch (OperationCanceledException) when (run.IsCancellationRequested) { }
                catch (Exception) when (retryRouter) { }
                if (token.IsCancellationRequested) break;
                if (retryRouter)
                {
                    await delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    continue;
                }
                if (refresh)
                {
                    var updatedDisplay = peer.Display;
                    var metadataChanged = !Equals(updatedDisplay, attachedDisplay);
                    if (!metadataChanged && topologyChanged)
                    {
                        // Moving/rearranging monitors in Windows must only rebind the capture rectangle.
                        // Recreating SudoVDA here makes ordinary layout edits destroy the monitor and loses
                        // the user's chosen desktop position.
                        log?.Invoke("Windows display topology changed; rebinding input to the existing virtual monitor.");
                    }
                    else if (updatedDisplay is null)
                    {
                        log?.Invoke("Remote display disappeared; removing the virtual monitor.");
                        await controller.DetachAsync(token).ConfigureAwait(false);
                        attachment = null;
                        attachedDisplay = null;
                    }
                    else if (metadataChanged)
                    {
                        log?.Invoke("Remote display metadata changed; refreshing the virtual monitor.");
                        attachment = await controller.AttachAsync(updatedDisplay, token).ConfigureAwait(false);
                        attachedDisplay = updatedDisplay;
                    }
                }
            }
        }
        finally
        {
            peer.Changed -= Changed;
            changes.Writer.TryComplete();
        }
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
