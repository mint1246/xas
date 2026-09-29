using Xas.Input;
using System.ComponentModel;

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
        if (new WindowsCaptureRegion(0, 0, 1920, 1080).MapToRemote(1919, 1079, 1536, 864) != (1535, 863))
            throw new Exception("A native-mode monitor rectangle must map its last physical pixel to the logical desktop edge.");
        if (WindowsMonitorTopology.FindRemote([physical, virtualMonitor]) != virtualMonitor)
            throw new Exception("The XAS virtual monitor was not found.");

        // A SudoVDA monitor is named "Generic PnP Monitor" and reports a manufacturer hardware ID, so it can
        // only be recognised through the virtual adapter that owns it. No configuration should be required.
        var sudovda = virtualMonitor with
        {
            FriendlyName = "Generic PnP Monitor",
            HardwareId = @"MONITOR\SMKD1CE\{4d36e968-e325-11ce-bfc1-08002be10318}\0001",
            AdapterHardwareId = @"root\sudomaker\sudovda"
        };
        if (WindowsMonitorTopology.FindRemote([physical, sudovda]) != sudovda)
            throw new Exception("A monitor on a virtual display adapter must be found without configuration.");
        var hardware = sudovda with { AdapterHardwareId = @"PCI\VEN_10DE&DEV_2484" };
        if (WindowsMonitorTopology.FindRemote([physical, hardware]) is not null)
            throw new Exception("A monitor on a hardware adapter must not take input ownership.");

        // A second virtual monitor on the same adapter makes automatic selection ambiguous, but an explicit
        // hardware ID must still resolve. This is how a daemon pins itself to the monitor it created.
        var other = sudovda with
        {
            DeviceName = @"\\.\DISPLAY3",
            HardwareId = @"MONITOR\SMKD1CE\{4d36e968-e325-11ce-bfc1-08002be10318}\0009",
            Region = new(5360, 0, 6784, 720)
        };
        if (WindowsMonitorTopology.FindRemote([physical, sudovda, other]) is not null)
            throw new Exception("Two virtual monitors must not be resolved without a hint.");
        if (WindowsMonitorTopology.FindRemote([physical, sudovda, other], other.HardwareId) != other)
            throw new Exception("An explicit monitor hardware ID must select that monitor.");
        if (WindowsMonitorTopology.FindRemote([physical, sudovda, virtualMonitor]) is not null)
            throw new Exception("An ambiguous monitor set must not guess which one owns input.");
        if (WindowsMonitorTopology.FindRemote([physical, virtualMonitor with
            { Region = new(1600, 0, 3520, 1080) }]) is not null)
            throw new Exception("An overlapping monitor must not take input ownership.");
        if (WindowsMonitorTopology.FindRemote([physical, virtualMonitor], "\\\\.\\DISPLAY1") != physical)
            throw new Exception("Explicit monitor selection failed.");
        if (WindowsMonitorTopology.FindRemote([virtualMonitor]) is not null)
            throw new Exception("A remote monitor without a local return target is unsafe.");

        // Windows may briefly reject monitor enumeration during a topology transition. That must retain
        // the current attachment, and the next successful pass must still observe a real removal.
        var passes = 0;
        bool EnumerateWithTransientFailure() => WindowsMonitorTopology.IsPresent(() =>
        {
            if (passes++ == 0) throw new Win32Exception("transient topology failure");
            return [physical];
        }, virtualMonitor.DeviceName, virtualMonitor.HardwareId);
        if (!EnumerateWithTransientFailure())
            throw new Exception("A transient Windows topology failure must not be treated as monitor removal.");
        if (EnumerateWithTransientFailure())
            throw new Exception("A subsequent successful topology pass must detect that the monitor was removed.");
        return Task.CompletedTask;
    }
}
