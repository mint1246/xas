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
}
