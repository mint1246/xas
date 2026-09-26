namespace Xas.Daemon;

/// <summary>
/// Restores standard user-session endpoints that are sometimes omitted from a systemd --user
/// service environment. Values explicitly provided by the desktop/session are never replaced.
/// </summary>
internal static class LinuxSessionEnvironment
{
    public static void Ensure()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtime) || !Directory.Exists(runtime)) return;

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")) &&
            File.Exists(Path.Combine(runtime, "bus")))
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=" + Path.Combine(runtime, "bus"));

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return;
        try
        {
            var socket = Directory.EnumerateFileSystemEntries(runtime, "wayland-*")
                .Select(Path.GetFileName)
                .Where(name => name is not null && name.StartsWith("wayland-", StringComparison.Ordinal) &&
                    name.AsSpan("wayland-".Length).IndexOfAnyExceptInRange('0', '9') < 0)
                .OrderBy(name => name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(socket)) Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", socket);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
