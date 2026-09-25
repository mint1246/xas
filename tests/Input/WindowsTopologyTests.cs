using Xas.Input;

namespace Xas.Tests;

public static class WindowsTopologyTests
{
    public static Task RunAsync()
    {
        var physical = new WindowsMonitor("\\\\.\\DISPLAY1", "Physical", "MONITOR\\ABC",
            new(0, 0, 1920, 1080));
        var virtualMonitor = new WindowsMonitor("\\\\.\\DISPLAY2", "XAS Linux Display", "MONITOR\\XAS",
            new(1920, 0, 3840, 1080));
        if (!virtualMonitor.Region.Contains(1920, 0) || virtualMonitor.Region.Contains(3840, 0))
            throw new Exception("Monitor rectangles must use half-open Windows screen bounds.");
        if (virtualMonitor.Region.MapToRemote(1920, 0, 2560, 1440) != (0, 0) ||
            virtualMonitor.Region.MapToRemote(3839, 1079, 2560, 1440) != (2559, 1439))
            throw new Exception("Monitor coordinates must scale to the remote display without leaving bounds.");
        if (WindowsMonitorTopology.FindRemote([physical, virtualMonitor]) != virtualMonitor)
            throw new Exception("The XAS virtual monitor was not found.");
        if (WindowsMonitorTopology.FindRemote([physical, virtualMonitor with
            { Region = new(1600, 0, 3520, 1080) }]) is not null)
            throw new Exception("An overlapping monitor must not take input ownership.");
        if (WindowsMonitorTopology.FindRemote([physical, virtualMonitor], "\\\\.\\DISPLAY1") != physical)
            throw new Exception("Explicit monitor selection failed.");
        if (WindowsMonitorTopology.FindRemote([virtualMonitor]) is not null)
            throw new Exception("A remote monitor without a local return target is unsafe.");
        return Task.CompletedTask;
    }
}
