using Xas.Core.FileSystem;
using Xas.Daemon.FileSystem.Mount;

namespace Xas.Tests;

public static class RemoteMountPathsTests
{
    public static Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return Task.CompletedTask;
        var root = Path.Combine(Path.GetTempPath(), "xas-mount-paths");
        var paths = new RemoteMountPaths(root);
        var drive = new RemoteVolume("drive-f", "F:\\", "removable", false);
        var first = paths.GetMountPoint("desktop-1", "DESKTOP-77IJ101", drive);
        Equal(Path.Combine(root, "DESKTOP-77IJ101", "F:"), first);
        // Reconnects and renamed metadata must not move a reserved path.
        Equal(first, paths.GetMountPoint("desktop-1", "renamed", drive with { Name = "renamed" }));
        var otherDrive = paths.GetMountPoint("desktop-1", "DESKTOP-77IJ101", drive with { Id = "drive-f-2" });
        Assert(otherDrive != first && Path.GetFileName(otherDrive).StartsWith("F:-"), "Duplicate volume names collided.");
        var otherDevice = paths.GetMountPoint("desktop-2", "DESKTOP-77IJ101", drive);
        Assert(Path.GetDirectoryName(otherDevice) != Path.GetDirectoryName(first), "Duplicate computer names collided.");
        var unsafePath = paths.GetMountPoint("unsafe", "../a/b", drive with { Name = "../../outside" });
        Assert(unsafePath.StartsWith(root + Path.DirectorySeparatorChar) &&
               Path.GetRelativePath(root, unsafePath).Split(Path.DirectorySeparatorChar).Length == 2,
            "A remote name escaped the two-component mount directory.");
        var sanitized = paths.GetMountPoint("sanitized", "a/b", drive with { Name = "a/b" });
        var sanitizedCollision = paths.GetMountPoint("sanitized", "a/b", drive with { Id = "another", Name = "a\\b" });
        Assert(sanitized != sanitizedCollision, "Sanitized volume names collided.");
        var blank = paths.GetMountPoint("blank", "..", drive with { Name = ".." });
        Equal(Path.Combine(root, "remote", "remote"), blank);
        return Task.CompletedTask;
    }

    private static void Equal(string expected, string actual) =>
        Assert(expected == actual, $"Expected '{expected}', got '{actual}'.");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
