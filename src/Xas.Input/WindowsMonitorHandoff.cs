using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Security;
using Xas.Input.Display;

namespace Xas.Input;

/// <summary>
/// Follows the native Windows cursor into the XAS virtual monitor and hands input back on exit. The monitor
/// is created automatically at the paired Linux display's own mode, so no display configuration, driver
/// setup, or environment variable is needed before handoff works.
/// </summary>
public static class WindowsMonitorHandoff
{
    /// <summary>How long to wait before retrying a failed or missing virtual display attach.</summary>
    private static readonly TimeSpan AttachRetryInterval = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(Func<ConfiguredPeer?> resolvePeer, DeviceIdentity identity,
        PeerTrustStore trust, TextWriter status, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return;
        // Attach is retried on a timer, so report each distinct condition once instead of every retry.
        var lastNotice = string.Empty;
        void Notice(string message)
        {
            lastNotice = message;
            status.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }

        IVirtualDisplayController? controller = null;
        try
        {
            var nextAttachAttempt = DateTime.MinValue;
            // The monitor this daemon created is the handoff boundary. Pinning it by hardware ID keeps a
            // monitor left on the adapter by another owner, or by an earlier crash, from taking input
            // ownership, and it is what lets a reattach be detected. Null until the first successful attach.
            string? ownedMonitorId = null;
            while (!token.IsCancellationRequested)
            {
                // Own the boundary first. Waiting for an unowned monitor to appear first would mean never
                // attaching at all while another application already holds a virtual monitor on the adapter.
                if (ownedMonitorId is not null && !WindowsMonitorTopology.Exists(ownedMonitorId))
                {
                    Notice("The virtual display was removed; recreating it.");
                    ownedMonitorId = null;
                }
                if (ownedMonitorId is null && DateTime.UtcNow >= nextAttachAttempt)
                {
                    nextAttachAttempt = DateTime.UtcNow + AttachRetryInterval;
                    controller ??= CreateController(Notice);
                    if (controller is not null)
                        ownedMonitorId = await TryAttachAsync(controller, resolvePeer, identity, trust,
                            Notice, token).ConfigureAwait(false);
                }

                WindowsMonitor? remote = null;
                try { remote = WindowsMonitorTopology.FindRemote(ownedMonitorId); }
                catch (Exception ex) { Notice($"Virtual monitor discovery failed: {ex.Message}"); }

                if (remote is null)
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    continue;
                }
                lastNotice = string.Empty;
                if (!WindowsMonitorTopology.TryGetPointer(out var x, out var y) || !remote.Region.Contains(x, y))
                { await Task.Delay(20, token).ConfigureAwait(false); continue; }

                try
                {
                    var peer = resolvePeer() ?? throw new InvalidOperationException(
                        "No default paired device is configured for the virtual monitor.");
                    Notice($"Input handoff starting on {remote.DeviceName} region " +
                        $"({remote.Region.Left},{remote.Region.Top})-({remote.Region.Right},{remote.Region.Bottom}).");
                    await InputControlClient.RunAsync(peer, identity, trust, token, remote.Region,
                        ownedMonitorId, Notice).ConfigureAwait(false);
                    Notice("Input handoff finished.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { Notice($"Virtual monitor input handoff ended: {ex.Message}"); }
                finally { WindowsMonitorTopology.ReturnToLocal(remote); }
                await Task.Delay(150, token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (controller is not null)
            {
                try { await controller.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { status.WriteLine($"Virtual display shutdown failed: {ex.Message}"); }
            }
        }
    }

    private static IVirtualDisplayController? CreateController(Action<string> notice)
    {
        var controller = new SudoVdaDisplayController(notice);
        if (controller.IsAvailable) return controller;
        notice("No virtual display driver is installed; install SudoVDA to enable automatic handoff.");
        return null;
    }

    /// <summary>Creates the virtual monitor, returning its hardware ID so handoff can be pinned to it.</summary>
    private static async Task<string?> TryAttachAsync(IVirtualDisplayController controller,
        Func<ConfiguredPeer?> resolvePeer, DeviceIdentity identity, PeerTrustStore trust,
        Action<string> notice, CancellationToken token)
    {
        if (resolvePeer() is not { } peer)
        {
            notice("No default paired Linux device; the virtual display stays detached.");
            return null;
        }
        DisplayMetadata? display;
        try { display = await RemoteDisplayProbe.TryFetchAsync(peer, identity, trust, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            notice($"Could not read the remote display mode: {ex.Message}");
            return null;
        }
        if (display is null)
        {
            notice("The paired device does not expose a usable display mode; grant Input access.");
            return null;
        }
        try
        {
            var attached = await controller.AttachAsync(display, token).ConfigureAwait(false);
            return attached.MonitorHardwareId;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex) { notice($"Virtual display attach failed: {ex.Message}"); return null; }
    }
}
