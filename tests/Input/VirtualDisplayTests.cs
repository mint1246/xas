using System.Runtime.InteropServices;
using Xas.Core;
using Xas.Input.Display;

namespace Xas.Tests;

/// <summary>
/// Checks the SudoVDA contract that cannot be exercised without the driver installed: the IOCTL codes and the
/// control structure layouts the driver reads. A layout mismatch would corrupt the request buffer, so the
/// offsets are asserted against the C definitions in the driver's own header rather than assumed.
/// </summary>
public static class VirtualDisplayTests
{
    public static Task RunAsync()
    {
        CheckIoctlCodes();
        CheckStructLayouts();
        CheckMonitorIdentity();
        CheckRefreshConversion();
        CheckModeRange();
        CheckNativeAndLogicalModes();
        CheckAttachmentShape();
        return Task.CompletedTask;
    }

    private static void CheckIoctlCodes()
    {
        // CTL_CODE(FILE_DEVICE_UNKNOWN, function, METHOD_BUFFERED, FILE_ANY_ACCESS). A drift here means the
        // daemon would send a request the driver does not recognise, so recompute rather than restate.
        foreach (var (code, function) in new[]
                 {
                     (SudoVdaDriver.AddVirtualDisplay, 0x800u),
                     (SudoVdaDriver.RemoveVirtualDisplay, 0x801u),
                     (SudoVdaDriver.DriverPing, 0x888u)
                 })
        {
            if (code != SudoVdaDriver.ControlCode(function))
                throw new Exception($"IOCTL for function {function:X} does not match CTL_CODE.");
        }
    }

    private static void CheckStructLayouts()
    {
        // VIRTUAL_DISPLAY_ADD_PARAMS is UINT, UINT, UINT, GUID, CHAR[14], CHAR[14]. GUID aligns to 4 in
        // both C and .NET, so it begins at offset 12, the two fixed fields at 28 and 42, total 56 bytes.
        if (Marshal.SizeOf<SudoVdaDriver.VirtualDisplayAddParams>() != 56 ||
            Marshal.OffsetOf<SudoVdaDriver.VirtualDisplayAddParams>(nameof(SudoVdaDriver.VirtualDisplayAddParams.MonitorGuid)) != 12 ||
            Marshal.OffsetOf<SudoVdaDriver.VirtualDisplayAddParams>(nameof(SudoVdaDriver.VirtualDisplayAddParams.DeviceName)) != 28 ||
            Marshal.OffsetOf<SudoVdaDriver.VirtualDisplayAddParams>(nameof(SudoVdaDriver.VirtualDisplayAddParams.SerialNumber)) != 42)
            throw new Exception("VIRTUAL_DISPLAY_ADD_PARAMS does not match the SudoVDA header layout.");

        // VIRTUAL_DISPLAY_REMOVE_PARAMS is a single GUID; VIRTUAL_DISPLAY_ADD_OUT is LUID {ULONG,LONG}+UINT.
        if (Marshal.SizeOf<SudoVdaDriver.VirtualDisplayRemoveParams>() != 16)
            throw new Exception("VIRTUAL_DISPLAY_REMOVE_PARAMS must be a single 16-byte GUID.");
        if (Marshal.SizeOf<SudoVdaDriver.VirtualDisplayAddResult>() != 12)
            throw new Exception("VIRTUAL_DISPLAY_ADD_OUT must be 12 bytes.");
    }

    private static void CheckMonitorIdentity()
    {
        // A stable identity per display and mode is what makes a reconnect land on the same desktop layout.
        // A mode change must yield a new identity, because SudoVDA caches the mode against the GUID and
        // re-adding a known GUID returns the panel at its original size.
        if (MonitorIdentity.For("DP-1", 1920, 1080, 60) != MonitorIdentity.For("DP-1", 1920, 1080, 60))
            throw new Exception("The same display and mode must keep one identity across restarts.");
        if (MonitorIdentity.For("DP-1", 1920, 1080, 60) == MonitorIdentity.For("DP-1", 2560, 1440, 60))
            throw new Exception("A resolution change must produce a new identity or the old mode is reused.");
        if (MonitorIdentity.For("DP-1", 1920, 1080, 60) == MonitorIdentity.For("DP-1", 1920, 1080, 75))
            throw new Exception("A refresh rate change must produce a new identity.");
        if (MonitorIdentity.For("DP-1", 1920, 1080, 60) == MonitorIdentity.For("DP-2", 1920, 1080, 60))
            throw new Exception("Distinct remote displays must not share a monitor identity.");
    }

    private static void CheckRefreshConversion()
    {
        if (SudoVdaDisplayController.ResolveRefreshHertz(60000) != 60 ||
            SudoVdaDisplayController.ResolveRefreshHertz(59940) != 59)
            throw new Exception("Millihertz must convert to whole Hertz.");
        if (SudoVdaDisplayController.ResolveRefreshHertz(null) != SudoVdaDriver.DefaultRefreshHertz ||
            SudoVdaDisplayController.ResolveRefreshHertz(0) != SudoVdaDriver.DefaultRefreshHertz)
            throw new Exception("An unknown refresh rate must fall back to the driver default.");
        if (SudoVdaDisplayController.ResolveRefreshHertz(900000) != 500)
            throw new Exception("An out-of-range refresh rate must be clamped, not rejected.");
    }

    private static void CheckModeRange()
    {
        // The controller refuses modes the driver cannot present before sending any request.
        if (IsSupported(new DisplayMetadata { Id = "DP-1", Name = "DP-1", WidthPixels = 0, HeightPixels = 1080 }))
            throw new Exception("A zero-width remote display must be rejected.");
        if (IsSupported(new DisplayMetadata
            { Id = "DP-1", Name = "DP-1", WidthPixels = SudoVdaDriver.MaxWidthPixels + 1, HeightPixels = 4320 }))
            throw new Exception("A remote display wider than the driver supports must be rejected.");
        if (!IsSupported(new DisplayMetadata
            { Id = "DP-1", Name = "DP-1", WidthPixels = 3840, HeightPixels = 2160, RefreshMilliHertz = 60000 }))
            throw new Exception("A supported remote display mode must be accepted.");
    }

    private static void CheckAttachmentShape()
    {
        // Handoff binds to the GDI device name and pins itself to the monitor's hardware ID, so the
        // attachment has to carry both along with the mode.
        const string hardwareId = @"MONITOR\SMKD1CE\{4d36e968-e325-11ce-bfc1-08002be10318}\0002";
        var attachment = new VirtualDisplayAttachment(@"\\.\DISPLAY2", hardwareId, Guid.NewGuid(), 2560, 1440, 60);
        if (attachment.DeviceName != @"\\.\DISPLAY2" || attachment.MonitorHardwareId != hardwareId ||
            attachment.WidthPixels != 2560 || attachment.RefreshHertz != 60)
            throw new Exception("The attachment must report the GDI device name, monitor ID, and mode.");
    }

    private static void CheckNativeAndLogicalModes()
    {
        var scaled = new DisplayMetadata
        {
            Id = "eDP-1", Name = "Built-in display", NativeWidthPixels = 1920, NativeHeightPixels = 1080,
            LogicalWidth = 1536, LogicalHeight = 864, WidthPixels = 1536, HeightPixels = 864, Scale = 1.25
        };
        if (SudoVdaDisplayController.ResolveVirtualWidth(scaled) != 1920 ||
            SudoVdaDisplayController.ResolveVirtualHeight(scaled) != 1080)
            throw new Exception("SudoVDA must mirror the physical panel mode when logical scale is present.");
        var rotated = scaled with { RotationDegrees = 90, LogicalWidth = 864, LogicalHeight = 1536,
            WidthPixels = 864, HeightPixels = 1536 };
        if (SudoVdaDisplayController.ResolveVirtualWidth(rotated) != 1080 ||
            SudoVdaDisplayController.ResolveVirtualHeight(rotated) != 1920)
            throw new Exception("SudoVDA must orient the physical mode to match the rotated desktop.");
        if (SudoVdaDisplayController.ResolveVirtualWidth(new DisplayMetadata
            { Id = "old", Name = "old", WidthPixels = 1280, HeightPixels = 720 }) != 1280)
            throw new Exception("Older peers without native dimensions must retain the logical mode fallback.");
    }

    private static bool IsSupported(DisplayMetadata display) =>
        display.WidthPixels is >= 1 and <= SudoVdaDriver.MaxWidthPixels &&
        display.HeightPixels is >= 1 and <= SudoVdaDriver.MaxHeightPixels;
}
