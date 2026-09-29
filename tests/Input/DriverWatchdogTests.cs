using Xas.Input.Display;

namespace Xas.Tests;

/// <summary>Exercises driver keepalive lifetime with a fake ping and no monitor or device access.</summary>
public static class DriverWatchdogTests
{
    public static async Task RunAsync()
    {
        var pings = 0;
        var watchdog = new DriverWatchdog(() => { Interlocked.Increment(ref pings); return true; },
            TimeSpan.FromMilliseconds(15));

        watchdog.Start();
        if (Volatile.Read(ref pings) != 1)
            throw new Exception("The watchdog must ping synchronously before attachment work starts.");
        watchdog.Start();
        if (Volatile.Read(ref pings) != 1)
            throw new Exception("Starting an active watchdog must not create another ping loop.");

        // Simulate slow Add/publication/position restoration; keepalives must continue throughout it.
        await Task.Delay(100).ConfigureAwait(false);
        if (Volatile.Read(ref pings) < 3)
            throw new Exception("Keepalives stopped during slow attachment work.");

        await watchdog.StopAsync().ConfigureAwait(false);
        var stoppedAt = Volatile.Read(ref pings);
        await Task.Delay(50).ConfigureAwait(false);
        if (Volatile.Read(ref pings) != stoppedAt)
            throw new Exception("Stopping the watchdog must cancel and join its ping loop.");

        // A completed watchdog can be restarted for a later AttachAsync call.
        watchdog.Start();
        if (Volatile.Read(ref pings) != stoppedAt + 1)
            throw new Exception("Restarting a stopped watchdog must issue one immediate ping.");
        await watchdog.StopAsync().ConfigureAwait(false);
    }
}
