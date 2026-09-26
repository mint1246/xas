using Xas.Core;
using Xas.Input;
using Xas.Input.Display;

namespace Xas.Cli;

/// <summary>
/// Local Windows display diagnostics. The daemon attaches the virtual monitor on its own, so this exists to
/// confirm the driver was found and to show what the handoff logic currently sees.
/// </summary>
internal static class DisplayCommands
{
    public static int Run(string[] args)
    {
        if (args.Length != 1) return Usage("Usage: xas display");
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The virtual display driver is a Windows component.");
            return 1;
        }

        var controller = new SudoVdaDisplayController(Console.Error.WriteLine);
        Console.WriteLine($"Driver installed: {(controller.IsAvailable ? "yes" : "no")}");
        if (controller.IsAvailable)
            Console.WriteLine($"Adapter: {WindowsMonitorTopology.FindAdapterDeviceName("root\\sudomaker\\sudovda") ?? "not present"}");

        var monitors = WindowsMonitorTopology.Enumerate();
        var virtuals = monitors.Where(VirtualDisplayDrivers.IsVirtualMonitor).ToArray();
        var remote = WindowsMonitorTopology.FindRemote(monitors);
        Console.WriteLine($"Monitors: {monitors.Count} ({virtuals.Length} virtual)");
        foreach (var monitor in monitors)
            Console.WriteLine($"  {monitor.DeviceName,-16} {monitor.FriendlyName} [{monitor.HardwareId}] " +
                $"{monitor.Region.Right - monitor.Region.Left}x{monitor.Region.Bottom - monitor.Region.Top}" +
                $" at ({monitor.Region.Left},{monitor.Region.Top})" +
                (VirtualDisplayDrivers.IsVirtualMonitor(monitor) ? "  virtual" : string.Empty) +
                (ReferenceEquals(monitor, remote) ? "  <- handoff target" : string.Empty));
        Explain(remote is not null, monitors, virtuals);
        return 0;
    }

    /// <summary>
    /// Says which precondition is missing. The daemon pins the monitor it created, so it can be armed even
    /// when an unrelated virtual monitor makes the unpinned choice here ambiguous.
    /// </summary>
    private static void Explain(bool ready, IReadOnlyList<WindowsMonitor> monitors, WindowsMonitor[] virtuals)
    {
        if (ready) { Console.WriteLine("Handoff is armed: the target above owns input while the cursor is over it."); return; }
        if (virtuals.Length > 1)
        {
            Console.WriteLine("Several virtual monitors are present. The daemon pins the one it created, so " +
                "handoff can still be armed, but this view cannot tell which. A monitor left by a crashed " +
                "daemon cannot be removed by software: disable and re-enable the SudoVDA display adapter in " +
                "Device Manager, or reboot.");
            return;
        }
        if (virtuals.Length == 0)
        {
            Console.WriteLine("No virtual display is present. The Windows daemon creates one automatically " +
                "once a paired Linux device reports its display mode; check its output for a failure.");
            return;
        }
        if (monitors.Count < 2)
        {
            Console.WriteLine("Handoff is inactive: the desktop is not extended. Press Win+P and choose Extend.");
            return;
        }
        Console.WriteLine("Handoff is inactive: the virtual monitor overlaps another one, so input ownership " +
            "would be ambiguous. Move it beside a physical monitor in Settings > Display.");
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
