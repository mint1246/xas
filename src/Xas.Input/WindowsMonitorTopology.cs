using System.ComponentModel;
using System.Runtime.InteropServices;
using Xas.Input.Display;

namespace Xas.Input;

public sealed record WindowsMonitor(string DeviceName, string FriendlyName, string HardwareId,
    WindowsCaptureRegion Region, string? AdapterHardwareId = null);

/// <summary>Reads Windows' native extended-desktop monitor rectangles and identifies the XAS IDD.</summary>
public static class WindowsMonitorTopology
{
    public static IReadOnlyList<WindowsMonitor> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var adapters = MapAdapterHardwareIds();
        var monitors = new List<WindowsMonitor>();
        MonitorEnum callback = (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(handle, ref info)) return true;
            var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            string name = string.Empty, id = string.Empty;
            if (EnumDisplayDevices(info.DeviceName, 0, ref device, 0))
            { name = device.DeviceString ?? string.Empty; id = device.DeviceId ?? string.Empty; }
            adapters.TryGetValue(info.DeviceName, out var adapter);
            monitors.Add(new WindowsMonitor(info.DeviceName, name, id,
                new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom), adapter));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate desktop monitors.");
        return monitors;
    }

    /// <summary>
    /// Returns the GDI device name of the first display adapter whose hardware ID matches, for example
    /// <c>\\.\DISPLAY2</c>. A software adapter holds every monitor it creates, so this identifies the whole
    /// virtual monitor set without depending on the friendly name a driver happens to publish.
    /// </summary>
    public static string? FindAdapterDeviceName(string hardwareId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        for (uint index = 0; ; index++)
        {
            var adapter = CreateAdapter();
            if (!EnumDisplayDevices(null, index, ref adapter, 0)) return null;
            if (adapter.DeviceId.StartsWith(hardwareId, StringComparison.OrdinalIgnoreCase)) return adapter.DeviceName;
        }
    }

    public static WindowsMonitor? FindRemote(IReadOnlyList<WindowsMonitor> monitors,
        string? deviceNameOverride = null)
    {
        if (monitors.Count < 2) return null;
        var matches = monitors.Where(m => Matches(m, deviceNameOverride)).ToArray();
        if (matches.Length != 1) return null;
        var remote = matches[0];
        if (remote.Region.Right <= remote.Region.Left || remote.Region.Bottom <= remote.Region.Top)
            return null;
        if (monitors.Where(m => !ReferenceEquals(m, remote)).Any(m => Overlaps(m.Region, remote.Region)))
            return null; // Cloned or overlapping layouts cannot reliably select input ownership.
        return remote;
    }

    /// <summary>
    /// Picks the monitor that handoff owns. With no hint, any monitor published by a virtual display driver
    /// qualifies, so no configuration is needed. A hint narrows the choice to a named device, friendly name,
    /// or hardware ID, which is how a specific monitor is selected when several are present.
    /// </summary>
    private static bool Matches(WindowsMonitor monitor, string? hint) => hint is null
        ? monitor.FriendlyName.Contains("XAS", StringComparison.OrdinalIgnoreCase) ||
          monitor.HardwareId.Contains("XAS", StringComparison.OrdinalIgnoreCase) ||
          VirtualDisplayDrivers.IsVirtualAdapter(monitor.AdapterHardwareId)
        : monitor.DeviceName.Equals(hint, StringComparison.OrdinalIgnoreCase) ||
          monitor.FriendlyName.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
          monitor.HardwareId.Contains(hint, StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps each adapter's GDI device name to its PnP hardware ID.</summary>
    private static Dictionary<string, string> MapAdapterHardwareIds()
    {
        var adapters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            var adapter = CreateAdapter();
            if (!EnumDisplayDevices(null, index, ref adapter, 0)) break;
            adapters[adapter.DeviceName] = adapter.DeviceId;
        }
        return adapters;
    }

    private static DisplayDevice CreateAdapter() => new() { Size = (uint)Marshal.SizeOf<DisplayDevice>() };

    /// <summary>
    /// Reports whether a monitor with this hardware ID is still published. Used to notice that Windows or the
    /// driver dropped the monitor the daemon created, so it can be recreated instead of waited on forever.
    /// </summary>
    public static bool Exists(string hardwareId)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return Enumerate().Any(m => m.HardwareId.Equals(hardwareId, StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException) { return true; } // A transient enumeration failure is not a removal.
    }

    /// <summary>
    /// Finds the monitor that owns input. A hint selects one by device name, friendly name, or hardware ID and
    /// is used to pin a specific virtual monitor; without one, any monitor from a virtual display adapter
    /// qualifies, so no configuration is required.
    /// </summary>
    public static WindowsMonitor? FindRemote(string? deviceNameHint = null) => FindRemote(Enumerate(),
        deviceNameHint ?? Environment.GetEnvironmentVariable("XAS_VIRTUAL_DISPLAY_DEVICE"));

    public static bool TryGetPointer(out int x, out int y)
    {
        x = y = 0;
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var point)) return false;
        x = point.X; y = point.Y;
        return true;
    }

    public static bool ReturnToLocal(WindowsMonitor remote)
    {
        if (!TryGetPointer(out var x, out var y) || !remote.Region.Contains(x, y)) return true;
        var locals = Enumerate().Where(m => !m.DeviceName.Equals(remote.DeviceName,
            StringComparison.OrdinalIgnoreCase)).Select(m => m.Region)
            .Where(r => r.Right > r.Left && r.Bottom > r.Top)
            .Select(r => (X: Math.Clamp(x, r.Left, r.Right - 1),
                Y: Math.Clamp(y, r.Top, r.Bottom - 1)))
            .OrderBy(p => (long)(p.X - x) * (p.X - x) + (long)(p.Y - y) * (p.Y - y))
            .ToArray();
        return locals.Length != 0 && SetCursorPos(locals[0].X, locals[0].Y);
    }

    private static bool Overlaps(WindowsCaptureRegion a, WindowsCaptureRegion b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool MonitorEnum(
        IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr rect, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    // A null device name enumerates display adapters; a monitor's GDI name enumerates its display devices.
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices([MarshalAs(UnmanagedType.LPWStr)] string? deviceName, uint index,
        ref DisplayDevice device, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
}
