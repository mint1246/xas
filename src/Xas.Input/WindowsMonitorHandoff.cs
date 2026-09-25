using Xas.Core.Configuration;
using Xas.Core.Security;

namespace Xas.Input;

/// <summary>Follows the native Windows cursor into the XAS virtual monitor and hands input back on exit.</summary>
public static class WindowsMonitorHandoff
{
    public static async Task RunAsync(Func<ConfiguredPeer?> resolvePeer, DeviceIdentity identity,
        PeerTrustStore trust, TextWriter status, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return;
        while (!token.IsCancellationRequested)
        {
            WindowsMonitor? remote;
            try { remote = WindowsMonitorTopology.FindRemote(); }
            catch (Exception ex)
            {
                status.WriteLine($"Virtual monitor discovery failed: {ex.Message}");
                await Task.Delay(2000, token).ConfigureAwait(false);
                continue;
            }
            if (remote is null)
            { await Task.Delay(1000, token).ConfigureAwait(false); continue; }
            if (!WindowsMonitorTopology.TryGetPointer(out var x, out var y) || !remote.Region.Contains(x, y))
            { await Task.Delay(20, token).ConfigureAwait(false); continue; }

            try
            {
                var peer = resolvePeer() ?? throw new InvalidOperationException(
                    "No default paired device is configured for the virtual monitor.");
                await InputControlClient.RunAsync(peer, identity, trust, token, remote.Region).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { status.WriteLine($"Virtual monitor input handoff ended: {ex.Message}"); }
            finally { WindowsMonitorTopology.ReturnToLocal(remote); }
            await Task.Delay(150, token).ConfigureAwait(false);
        }
    }
}
