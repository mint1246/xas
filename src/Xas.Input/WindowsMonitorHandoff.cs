using System.Threading.Channels;
using Xas.Core;
using Xas.Input.Display;

namespace Xas.Input;

/// <summary>Owns the XAS virtual monitor and routes native input over an already authenticated peer session.</summary>
public static class WindowsMonitorHandoff
{
    public static async Task RunAsync(IHotInputPeer peer, CancellationToken token, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return;

        await using var controller = new SudoVdaDisplayController(log);
        if (!controller.IsAvailable)
        {
            log?.Invoke("No virtual display driver is installed; install SudoVDA to enable automatic handoff.");
            return;
        }

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
                    !WindowsMonitorTopology.Exists(attachment.MonitorHardwareId))
                {
                    log?.Invoke($"Attaching virtual display for {display.Name} at {display.WidthPixels}x{display.HeightPixels}.");
                    attachment = await controller.AttachAsync(display, token).ConfigureAwait(false);
                    attachedDisplay = display;
                }

                var monitor = FindOwnedRemote(attachment);
                if (monitor is null)
                {
                    log?.Invoke("The owned virtual display is not available in the Windows desktop topology.");
                    await WaitForChangeAsync(changes.Reader, token).ConfigureAwait(false);
                    continue;
                }

                using var run = CancellationTokenSource.CreateLinkedTokenSource(token);
                topologyChanged = false;
                var router = HotInputRouter.RunAsync(peer, monitor.Region, run.Token, TopologyChanged);
                var refresh = false;
                while (!token.IsCancellationRequested && !refresh)
                {
                    var changed = changes.Reader.WaitToReadAsync(token).AsTask();
                    var completed = await Task.WhenAny(router, changed).ConfigureAwait(false);
                    if (completed == router)
                    {
                        await router.ConfigureAwait(false);
                        // The hot router only completes normally for the emergency return or peer disconnect.
                        if (!token.IsCancellationRequested)
                        {
                            WindowsMonitorTopology.ReturnToLocal(monitor);
                            log?.Invoke("Input returned to the local desktop.");
                        }
                        break;
                    }
                    await changed.ConfigureAwait(false);
                    while (changes.Reader.TryRead(out _)) { }
                    if (topologyChanged || !Equals(peer.Display, attachedDisplay)) refresh = true;
                }
                run.Cancel();
                try { await router.ConfigureAwait(false); }
                catch (OperationCanceledException) when (run.IsCancellationRequested) { }
                if (token.IsCancellationRequested) break;
                if (refresh)
                {
                    log?.Invoke("Remote display metadata changed; refreshing the virtual monitor.");
                    var updatedDisplay = peer.Display;
                    if (updatedDisplay is null)
                    {
                        await controller.DetachAsync(token).ConfigureAwait(false);
                        attachment = null;
                        attachedDisplay = null;
                    }
                    else
                    {
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
        var owned = monitors.FirstOrDefault(m => m.HardwareId.Equals(attachment.MonitorHardwareId,
            StringComparison.OrdinalIgnoreCase));
        return owned is null ? null : WindowsMonitorTopology.FindRemote(monitors, owned.HardwareId);
    }

    private static async Task WaitForChangeAsync(ChannelReader<byte> changes, CancellationToken token)
    {
        await changes.WaitToReadAsync(token).ConfigureAwait(false);
        while (changes.TryRead(out _)) { }
    }
}
