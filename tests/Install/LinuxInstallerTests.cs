namespace Xas.Tests;

public static class LinuxInstallerTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "xas-installer-test-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package");
        var home = Path.Combine(root, "home");
        var mockBin = Path.Combine(root, "mock-bin");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(mockBin);
        try
        {
            File.WriteAllText(Path.Combine(package, "xas"), "fixture cli");
            File.WriteAllText(Path.Combine(package, "Xas.Daemon"), "fixture daemon");
            File.Copy(FindInstallerScript(), Path.Combine(package, "install-linux.sh"));
            var log = Path.Combine(root, "systemctl.log");
            var mock = Path.Combine(mockBin, "systemctl");
            File.WriteAllText(mock, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$XAS_SYSTEMCTL_LOG\"\ncase \"$*\" in\n  '--user is-active --quiet xas-daemon.service') [ \"${XAS_SERVICE_ACTIVE:-0}\" = 1 ] ;;\n  '--user restart xas-daemon.service') [ \"${XAS_FAIL_ACTION:-}\" != restart ] ;;\n  '--user enable --now xas-daemon.service') [ \"${XAS_FAIL_ACTION:-}\" != start ] ;;\n  *) exit 0 ;;\nesac\n");
            File.SetUnixFileMode(mock, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var script = Path.Combine(package, "install-linux.sh");
            var installDir = Path.Combine(root, "bin");
            var path = mockBin + ":" + Environment.GetEnvironmentVariable("PATH");

            var succeeded = await RunInstaller(script, package, home, installDir, path, log, active: true, failAction: "");
            if (succeeded.ExitCode != 0)
                throw new InvalidOperationException("Installer failed for the mocked active service: " + succeeded.Output);
            var calls = await File.ReadAllTextAsync(log);
            if (!calls.Contains("--user restart xas-daemon.service", StringComparison.Ordinal) ||
                calls.Contains("--user enable --now xas-daemon.service", StringComparison.Ordinal))
                throw new InvalidOperationException("An already-running daemon was not restarted exclusively: " + calls);

            await File.WriteAllTextAsync(log, "");
            var failed = await RunInstaller(script, package, home, installDir, path, log, active: true, failAction: "restart");
            if (failed.ExitCode == 0 || !failed.Output.Contains("Failed to restart the xas user daemon", StringComparison.Ordinal))
                throw new InvalidOperationException("A failed daemon restart did not fail the installer: " + failed.Output);

            await File.WriteAllTextAsync(log, "");
            var starts = await RunInstaller(script, package, home, installDir, path, log, active: false, failAction: "");
            calls = await File.ReadAllTextAsync(log);
            if (starts.ExitCode != 0 || !calls.Contains("--user enable --now xas-daemon.service", StringComparison.Ordinal) ||
                calls.Contains("--user restart xas-daemon.service", StringComparison.Ordinal))
                throw new InvalidOperationException("An inactive daemon was not enabled and started: " + starts.Output + calls);

            await File.WriteAllTextAsync(log, "");
            var startFailed = await RunInstaller(script, package, home, installDir, path, log, active: false, failAction: "start");
            if (startFailed.ExitCode == 0 || !startFailed.Output.Contains("Failed to enable and start the xas user daemon", StringComparison.Ordinal))
                throw new InvalidOperationException("A failed daemon start did not fail the installer: " + startFailed.Output);

            Directory.CreateDirectory(installDir);
            await File.WriteAllTextAsync(Path.Combine(installDir, "Xas.Daemon"), "previous daemon");
            var installShim = Path.Combine(mockBin, "install");
            File.WriteAllText(installShim, "#!/bin/sh\ncase \"$*\" in *Xas.Daemon*) exit 1 ;; esac\nexec /usr/bin/install \"$@\"\n");
            File.SetUnixFileMode(installShim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var replacementFailed = await RunInstaller(script, package, home, installDir, path, log, active: true, failAction: "");
            var retained = await File.ReadAllTextAsync(Path.Combine(installDir, "Xas.Daemon"));
            if (replacementFailed.ExitCode == 0 || retained != "previous daemon")
                throw new InvalidOperationException("A failed daemon binary replacement damaged the previous executable.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunInstaller(string script, string package, string home,
        string installDir, string path, string log, bool active, string failAction)
    {
        var start = new System.Diagnostics.ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(script);
        start.Environment["HOME"] = home;
        start.Environment["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share");
        start.Environment["XAS_INSTALL_DIR"] = installDir;
        start.Environment["XAS_SYSTEMCTL_LOG"] = log;
        start.Environment["PATH"] = path;
        start.Environment["XAS_SERVICE_ACTIVE"] = active ? "1" : "0";
        start.Environment["XAS_FAIL_ACTION"] = failAction;
        var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start shell.");
        var output = await process.StandardOutput.ReadToEndAsync();
        output += await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }

    private static string FindInstallerScript()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "install-linux.sh");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate scripts/install-linux.sh from the current directory.");
    }
}
