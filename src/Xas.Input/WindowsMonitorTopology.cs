using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Xas.Input;

public sealed record WindowsMonitor(string DeviceName, string FriendlyName, string HardwareId,
    WindowsCaptureRegion Region);

/// <summary>Reads Windows' native extended-desktop monitor rectangles and identifies the XAS IDD.</summary>
public static class WindowsMonitorTopology
{
    public static IReadOnlyList<WindowsMonitor> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var monitors = new List<WindowsMonitor>();
        MonitorEnum callback = (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(handle, ref info)) return true;
            var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            string name = string.Empty, id = string.Empty;
            if (EnumDisplayDevices(info.DeviceName, 0, ref device, 0))
            { name = device.DeviceString; id = device.DeviceId; }
            monitors.Add(new WindowsMonitor(info.DeviceName, name, id,
                new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom)));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate desktop monitors.");
        return monitors;
    }

    public static WindowsMonitor? FindRemote(IReadOnlyList<WindowsMonitor> monitors,
        string? deviceNameOverride = null)
    {
        if (monitors.Count < 2) return null;
        var matches = monitors.Where(m => deviceNameOverride is not null
            ? m.DeviceName.Equals(deviceNameOverride, StringComparison.OrdinalIgnoreCase)
            : m.FriendlyName.Contains("XAS", StringComparison.OrdinalIgnoreCase) ||
                m.HardwareId.Contains("XAS", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return null;
        var remote = matches[0];
        if (remote.Region.Right <= remote.Region.Left || remote.Region.Bottom <= remote.Region.Top)
            return null;
        if (monitors.Where(m => !ReferenceEquals(m, remote)).Any(m => Overlaps(m.Region, remote.Region)))
            return null; // Cloned or overlapping layouts cannot reliably select input ownership.
        return remote;
    }

    public static WindowsMonitor? FindRemote() => FindRemote(Enumerate(),
        Environment.GetEnvironmentVariable("XAS_VIRTUAL_DISPLAY_DEVICE"));

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
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string deviceName, uint index, ref DisplayDevice device, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
}
