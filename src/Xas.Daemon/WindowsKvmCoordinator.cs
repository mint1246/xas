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

                // A task that failed after an asynchronous attach attempt also arrives here through its
                // completion signal. Remember that case before clearing the old task so retries are actually
                // throttled; the old code only delayed failures that completed synchronously in RunAsync().
                var retryingFailedHandoff = string.Equals(nextId, activeDeviceId, StringComparison.Ordinal) &&
                    activeTask.IsCompleted;

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
                if (retryingFailedHandoff)
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                activeDeviceId = session.DeviceId;
                activeStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                log?.Invoke($"KVM handoff targeting {configured!.Name} ({session.DeviceId}).");
                activeTask = WindowsMonitorHandoff.RunAsync(session, activeStop.Token, log);
                _ = activeTask.ContinueWith(_ => Signal(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
