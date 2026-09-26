using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Xas.Input.Display;

/// <summary>
/// Control channel of SudoVDA, the UMDF indirect display driver that Apollo uses. The IOCTL contract,
/// the device interface class, and the adapter hardware ID are fixed by the driver and are documented in
/// SudoMaker/SudoVDA and in the header vendored by ClassicOldSong/Apollo
/// (<c>third-party/sudovda/sudovda-ioctl.h</c>). All three are marked "DO NOT CHANGE" upstream.
/// </summary>
internal static class SudoVdaDriver
{
    /// <summary>Device interface class the driver registers with <c>WdfDeviceCreateDeviceInterface</c>.</summary>
    public static readonly Guid InterfaceClass = new("e5bcc234-1e0c-418a-a0d4-ef8b7501414d");

    /// <summary>Hardware ID the SudoVDA adapter reports from <c>EnumDisplayDevices</c>.</summary>
    public const string AdapterHardwareId = @"root\sudomaker\sudovda";

    /// <summary>Size of the driver's <c>DeviceName</c> and <c>SerialNumber</c> fields, terminator included.</summary>
    public const int NameFieldSize = 14;

    public const int MaxWidthPixels = 7680;
    public const int MaxHeightPixels = 4320;
    public const int DefaultRefreshHertz = 60;

    // CTL_CODE(FILE_DEVICE_UNKNOWN, function, METHOD_BUFFERED, FILE_ANY_ACCESS). The driver reserves the
    // 0x8xx function range, so the codes are fixed constants rather than computed at run time.
    internal const uint AddVirtualDisplay = 0x222000;   // function 0x800
    internal const uint RemoveVirtualDisplay = 0x222004; // function 0x801
    internal const uint DriverPing = 0x222220;          // function 0x888, the watchdog keepalive

    /// <summary>Recomputes <c>CTL_CODE</c> the way <c>winioctl.h</c> defines it, for verification.</summary>
    internal static uint ControlCode(uint function) => (0x22 /* FILE_DEVICE_UNKNOWN */ << 16) | (function << 2);

    private const int DigcfPresent = 0x00000002;
    private const int DigcfDeviceInterface = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int InvalidHandleValue = -1;
    private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
    private const uint OpenExisting = 3;

    /// <summary>Opens the driver's control device, or returns null when SudoVDA is not installed.</summary>
    public static SafeFileHandle? Open()
    {
        var interfaceClass = InterfaceClass;
        var devices = SetupDiGetClassDevs(ref interfaceClass, IntPtr.Zero, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (devices is 0 or InvalidHandleValue) return null;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = default(DeviceInterfaceData);
                data.Size = Marshal.SizeOf<DeviceInterfaceData>();
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref interfaceClass, index, ref data)) return null;
                var path = ReadDevicePath(devices, ref data);
                if (string.IsNullOrEmpty(path)) continue;
                // Shared access: Apollo and other clients may hold their own handle at the same time.
                var handle = CreateFile(path, 0xC0000000, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (!handle.IsInvalid) return handle;
                handle.Dispose();
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
    }

    /// <summary>Creates a monitor owned by <paramref name="monitorId"/> and reports the target it created.</summary>
    public static VirtualDisplayTarget Add(SafeFileHandle device, Guid monitorId, int width, int height,
        int refreshHertz, string deviceName)
    {
        var parameters = new VirtualDisplayAddParams
        {
            Width = (uint)width,
            Height = (uint)height,
            RefreshRate = (uint)refreshHertz,
            MonitorGuid = monitorId,
            DeviceName = ToFixedAscii(deviceName),
            SerialNumber = ToFixedAscii("XAS")
        };
        if (!DeviceIoControl(device, AddVirtualDisplay, ref parameters, (uint)Marshal.SizeOf<VirtualDisplayAddParams>(),
                out var result, (uint)Marshal.SizeOf<VirtualDisplayAddResult>(), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"SudoVDA rejected the {width}x{height}@{refreshHertz} virtual display request.");
        return new(result.LowPart, result.HighPart, result.TargetId);
    }

    /// <summary>Destroys a monitor previously created by <see cref="Add"/>.</summary>
    public static void Remove(SafeFileHandle device, Guid monitorId)
    {
        var parameters = new VirtualDisplayRemoveParams { MonitorGuid = monitorId };
        if (!DeviceIoControl(device, RemoveVirtualDisplay, ref parameters,
                (uint)Marshal.SizeOf<VirtualDisplayRemoveParams>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SudoVDA could not remove the virtual display.");
    }

    /// <summary>
    /// Keeps the driver's watchdog quiet. The default watchdog timeout is three seconds, so callers that
    /// own a monitor must ping more often than that or the driver tears the monitor down.
    /// </summary>
    public static bool Ping(SafeFileHandle device) =>
        DeviceIoControl(device, DriverPing, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    /// <summary>Writes an ASCII string into a fixed-size driver field, truncating and always terminating it.</summary>
    private static byte[] ToFixedAscii(string value)
    {
        var field = new byte[NameFieldSize];
        var count = Math.Min(value.Length, NameFieldSize - 1);
        for (var i = 0; i < count; i++)
        {
            var c = value[i];
            field[i] = c is >= ' ' and <= '~' ? (byte)c : (byte)'_';
        }
        return field;
    }

    private static string? ReadDevicePath(IntPtr devices, ref DeviceInterfaceData data)
    {
        // SP_DEVICE_INTERFACE_DETAIL_DATA_A is variable length: ask for the size, then read the path that
        // follows the header. The header size is 8 on 64-bit and 6 on 32-bit, and the path starts at +4.
        SetupDiGetDeviceInterfaceDetail(devices, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return null;
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(devices, ref data, buffer, required, out _, IntPtr.Zero)) return null;
            var path = Marshal.PtrToStringAnsi(buffer + 4);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal readonly record struct VirtualDisplayTarget(uint LuidLow, int LuidHigh, uint TargetId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct VirtualDisplayAddParams
    {
        public uint Width;
        public uint Height;
        public uint RefreshRate;
        public Guid MonitorGuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NameFieldSize)] public byte[] DeviceName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NameFieldSize)] public byte[] SerialNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VirtualDisplayRemoveParams
    {
        public Guid MonitorGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VirtualDisplayAddResult
    {
        public uint LowPart;
        public int HighPart;
        public uint TargetId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClass;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr deviceInfoData,
        ref Guid interfaceClass, uint index, ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref DeviceInterfaceData data,
        IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        ref VirtualDisplayAddParams input, uint inputSize, out VirtualDisplayAddResult output, uint outputSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        ref VirtualDisplayRemoveParams input, uint inputSize, IntPtr output, uint outputSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
}
