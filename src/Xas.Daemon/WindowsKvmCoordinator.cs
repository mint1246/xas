using System.Threading.Channels;
using Xas.Core.Configuration;
using Xas.Daemon.Sessions;
using Xas.Input;

namespace Xas.Daemon;

/// <summary>Keeps automatic Windows handoff attached to the currently configured default peer.</summary>
internal static class WindowsKvmCoordinator
{
    public static async Task RunAsync(LocalConfiguration configuration, PeerSessionManager sessions,
        CancellationToken cancellationToken, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        var wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
        void Signal() => wake.Writer.TryWrite(0);
        void SessionChanged(PeerSessionSnapshot _) => Signal();
        void SessionRemoved(string _) => Signal();
        configuration.Changed += Signal;
        sessions.SessionChanged += SessionChanged;
        sessions.SessionRemoved += SessionRemoved;

        CancellationTokenSource? activeStop = null;
        Task activeTask = Task.CompletedTask;
        string? activeDeviceId = null;
        try
        {
            Signal();
            while (!cancellationToken.IsCancellationRequested)
            {
                await wake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                while (wake.Reader.TryRead(out _)) { }

                var configured = configuration.Resolve(null);
                var session = configured is null ? null : sessions.GetSession(configured.DeviceId);
                var nextId = session?.DeviceId;
                if (string.Equals(nextId, activeDeviceId, StringComparison.Ordinal) && !activeTask.IsCompleted)
                    continue;

                if (activeStop is not null)
                {
                    activeStop.Cancel();
                    try { await activeTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (activeStop.IsCancellationRequested) { }
                    catch (Exception ex) { log?.Invoke($"KVM handoff stopped: {ex.Message}"); }
                    activeStop.Dispose();
                    activeStop = null;
                    activeTask = Task.CompletedTask;
                    activeDeviceId = null;
                }

                if (session is null) continue;
                activeDeviceId = session.DeviceId;
                activeStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                log?.Invoke($"KVM handoff targeting {configured!.Name} ({session.DeviceId}).");
                activeTask = WindowsMonitorHandoff.RunAsync(session, activeStop.Token, log);
                _ = activeTask.ContinueWith(_ => Signal(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                // A failed attach must not strand the daemon forever waiting for an unrelated peer/config
                // event. The completion continuation wakes this loop; throttle retries so a persistent
                // driver failure cannot become a tight attach loop.
                if (activeTask.IsCompleted)
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            configuration.Changed -= Signal;
            sessions.SessionChanged -= SessionChanged;
            sessions.SessionRemoved -= SessionRemoved;
            wake.Writer.TryComplete();
            if (activeStop is not null)
            {
                activeStop.Cancel();
                try { await activeTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (activeStop.IsCancellationRequested) { }
                catch (Exception ex) { log?.Invoke($"KVM handoff stopped: {ex.Message}"); }
                activeStop.Dispose();
            }
        }
    }
}
