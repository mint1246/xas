using Xas.Daemon.Display;

namespace Xas.Tests;

public static class DisplayMetadataTests
{
    public static Task RunAsync()
    {
        var x11 = LinuxDisplayMetadataService.ParseXrandr("""
            Screen 0: minimum 8 x 8, current 1920 x 1080, maximum 32767 x 32767
            eDP-1 connected 1920x1080+0+0 (normal left inverted right x axis y axis) 309mm x 174mm
               1920x1080 60.00*+ 59.94
            HDMI-1 connected primary 2560x1440+0+0 (normal left inverted right x axis y axis) 600mm x 340mm
               2560x1440 59.95*+
            """);
        Check(x11 is { Id: "HDMI-1", WidthPixels: 2560, HeightPixels: 1440, RefreshMilliHertz: 59950, PhysicalWidthMillimeters: 600, RotationDegrees: 0 }, "XRandR primary and mode parsing");

        var portrait = LinuxDisplayMetadataService.ParseWlrRandr("""
            eDP-1 "Panel" (focused)
              Physical size: 309x174 mm
              Enabled: yes
              Modes:
                1080x1920 px, 60.000000 Hz (current)
              Transform: 90
              Scale: 1.250000
            """);
        Check(portrait is { Id: "eDP-1", WidthPixels: 1920, HeightPixels: 1080, RefreshMilliHertz: 60000, RotationDegrees: 90, Scale: 1.25 }, "wlr-randr current mode parsing");

        var kde = LinuxDisplayMetadataService.ParseKScreenDoctor("""
            Output: 1 eDP-1 enabled connected priority 1 Panel
              Modes: 0: 1920x1080@60*! 1: 1920x1080@59.94
              Geometry: 0,0 1920x1080
              Scale: 1.25
              Rotation: 2
              Physical size: 309x174
            """);
        Check(kde is { Id: "eDP-1", WidthPixels: 1080, HeightPixels: 1920, RefreshMilliHertz: 60000, RotationDegrees: 90, PhysicalHeightMillimeters: 174 }, "KScreen output parsing");

        if (LinuxDisplayMetadataService.ParseXrandr("Screen 0: no connected outputs") is not null) throw new Exception("No display must not produce fabricated metadata.");
        return Task.CompletedTask;

        static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception($"Failed {what}.");
        }
    }

    /// <summary>
    /// Mutter GetCurrentState replies, captured from a GNOME 49 Wayland session. The panel runs 1920x1080 at
    /// scale 1.25 in logical layout, so the usable desktop is 1536x864: that divided value, not the panel mode,
    /// is the coordinate space absolute input injection has to address.
    /// </summary>
    public static class MutterDisplayTests
    {
        private const string ScaledLaptop = """
            (uint32 1, [(('eDP-1', 'AUO', '0x61ed', '0x00000000'), [('1920x1080@60.056', 1920, 1080, 60.056221008300781, 1.25, [1.0, 1.25, 2.0], {'is-current': <true>, 'is-preferred': <true>}), ('1920x1080@59.934', 1920, 1080, 59.933879852294922, 1.0, [1.0], {})], {'is-builtin': <true>, 'display-name': <'Built-in display'>, 'is-for-lease': <false>, 'color-mode': <uint32 0>, 'supported-color-modes': <[uint32 0, 2]>, 'rgb-range': <uint32 1>})], [(0, 0, 1.25, uint32 0, true, [('eDP-1', 'AUO', '0x61ed', '0x00000000')], @a{sv} {})], {'layout-mode': <uint32 1>, 'supports-changing-layout-mode': <true>})
            """;

        public static Task RunAsync()
        {
            var scaled = LinuxDisplayMetadataService.ParseMutterState(ScaledLaptop);
            if (scaled is not { Id: "eDP-1", Name: "Built-in display" } ||
                scaled.WidthPixels != 1536 || scaled.HeightPixels != 864 ||
                scaled.RefreshMilliHertz != 60056 || scaled.RotationDegrees != 0 || scaled.Scale != 1.25)
                throw new Exception($"Mutter logical layout must report 1536x864 at scale 1.25, got " +
                    $"{scaled?.WidthPixels}x{scaled?.HeightPixels} scale {scaled?.Scale}.");

            // Physical layout reports the panel mode itself and divides by nothing.
            var physical = LinuxDisplayMetadataService.ParseMutterState(
                ScaledLaptop.Replace("'layout-mode': <uint32 1>", "'layout-mode': <uint32 2>"));
            if (physical is not { WidthPixels: 1920, HeightPixels: 1080 })
                throw new Exception("Mutter physical layout must report the panel mode unchanged.");

            // A rotated logical monitor swaps its axes, and the transform code drives that.
            var rotated = LinuxDisplayMetadataService.ParseMutterState(
                ScaledLaptop.Replace("uint32 0, true, [('eDP-1'", "uint32 1, true, [('eDP-1'"));
            if (rotated is not { WidthPixels: 864, HeightPixels: 1536, RotationDegrees: 90 })
                throw new Exception("A 90 degree Mutter transform must swap the reported axes.");

            // Mutter lists every plugged output, so a display the user switched off in Settings is still in
            // the reply. It must be ignored rather than reported as the desktop.
            if (LinuxDisplayMetadataService.ParseMutterState(ScaledLaptop.Replace(
                "[(0, 0, 1.25, uint32 0, true, [('eDP-1', 'AUO', '0x61ed', '0x00000000')], @a{sv} {})]",
                "[]")) is not null)
                throw new Exception("A reply with no active logical monitor must not fabricate a display.");

            if (LinuxDisplayMetadataService.ParseMutterState(null) is not null ||
                LinuxDisplayMetadataService.ParseMutterState("Error: no such interface") is not null)
                throw new Exception("A failed Mutter call must not produce metadata.");

            return Task.CompletedTask;
        }
    }
}
