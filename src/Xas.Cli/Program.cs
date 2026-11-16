using Xas.Core;
using Xas.Core.LocalIpc;
using Xas.Cli.Terminal;

namespace Xas.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var localResult = await LocalSetupCommands.TryRunAsync(args);
            if (localResult is not null)
                return localResult.Value;
            using var standardInput = TerminalMode.OpenInputStream();
            using var client = new LocalDaemonClient(
                standardInput,
                Console.OpenStandardOutput(),
                Console.OpenStandardError());
            return await new XasCommandLine(client, Console.Out, Console.Error).RunAsync(args);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (Exception exception) when (IsExpectedCliError(exception))
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static bool IsExpectedCliError(Exception exception) => exception switch
    {
        ArgumentException => true,
        InvalidOperationException => true,
        InvalidDataException => true,
        IOException => true,
        NotSupportedException => true,
        UnauthorizedAccessException => true,
        TimeoutException => true,
        System.ComponentModel.Win32Exception => true,
        System.Security.Authentication.AuthenticationException => true,
        _ => false
    };
}

/// <summary>Transport boundary for the command line. A daemon transport can implement this without changing parsing.</summary>
public interface IXasClient
{
    Task<IReadOnlyList<KnownDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken);
    Task<DeviceInfo?> GetDeviceInfoAsync(string? deviceId, CancellationToken cancellationToken);
    Task SetDefaultDeviceAsync(string deviceId, CancellationToken cancellationToken);
    Task<bool> PingAsync(string? deviceId, CancellationToken cancellationToken);
    Task<int> RunShellAsync(ShellRequest request, string? deviceId, CancellationToken cancellationToken);
    Task<int> RunInteractiveAsync(string? deviceId, bool elevated, CancellationToken cancellationToken);
    Task<int> CopyAsync(string source, string destination, bool recursive, bool overwrite,
        CancellationToken cancellationToken);
    Task<int> SyncClipboardAsync(bool push, string? deviceId, CancellationToken cancellationToken);
    Task<int> WatchClipboardAsync(string? deviceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LocalRemoteVolumeInfo>> ListRemoteVolumesAsync(
        string? deviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalRemoteVolumeInfo>> ListRemoteMountsAsync(
        string? targetDeviceId, CancellationToken cancellationToken);

    Task<LocalRemoteVolumeInfo> MountRemoteVolumeAsync(
        string? targetDeviceId, string volume, CancellationToken cancellationToken);

    Task<bool> UnmountRemoteVolumeAsync(
        string? deviceId, string volume, CancellationToken cancellationToken);

    Task EjectRemoteVolumeAsync(string? deviceId, string volume, CancellationToken cancellationToken);
    Task<int> RunInputAsync(string? deviceId, CancellationToken cancellationToken);
}

public sealed class XasCommandLine(IXasClient client, TextWriter output, TextWriter error)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        string? targetDevice = null;
        var elevated = false;
        if (args.Length > 0 && args[0] == "-d")
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
            {
                error.WriteLine("Usage: xas -d <device-id> <command>");
                return 2;
            }
            targetDevice = args[1];
            args = args.Skip(2).ToArray();
        }

        if (args.Length > 0 && args[0] is "--sudo" or "--admin")
        {
            elevated = true;
            args = args.Skip(1).ToArray();
        }

        if (args.Length == 0)
        {
            try
            {
                return await client.RunInteractiveAsync(targetDevice, elevated, cancellationToken);
            }
            catch (XasClientException ex)
            {
                error.WriteLine(ex.Message);
                return 1;
            }
        }

        if (elevated && args[0].ToLowerInvariant() is not ("-c" or "exec"))
        {
            error.WriteLine("--sudo is only available for shell commands and interactive shells.");
            return 2;
        }

        try
        {
            return await DispatchCommandAsync(args, targetDevice, elevated, cancellationToken);
        }
        catch (XasClientException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private async Task<int> DispatchCommandAsync(
        string[] args, string? targetDevice, bool elevated, CancellationToken cancellationToken)
    {
        switch (args[0].ToLowerInvariant())
        {
            case "devices":
                return await ListDevicesAsync(cancellationToken);
            case "info":
                return await ShowInfoAsync(
                    ResolveTarget(targetDevice, args.Skip(1).ToArray()), cancellationToken);
            case "default":
                return await SetDefaultAsync(args, cancellationToken);
            case "ping":
                var pingTarget = ResolveTarget(targetDevice, args.Skip(1).ToArray());
                return await PingAsync(pingTarget, cancellationToken);
            case "-c":
                return await RunCommandAsync(args, targetDevice, elevated, cancellationToken);
            case "exec":
                return await RunExecutableAsync(args, targetDevice, elevated, cancellationToken);
            case "cp":
                return await CopyAsync(args, targetDevice, cancellationToken);
            case "clipboard":
                return await ClipboardAsync(args, targetDevice, cancellationToken);
            case "volumes":
                return await ListRemoteVolumesAsync(
                    ResolveTarget(targetDevice, args.Skip(1).ToArray()), cancellationToken);
            case "mounts":
                return await ListRemoteMountsAsync(
                    ResolveTarget(targetDevice, args.Skip(1).ToArray()), cancellationToken);
            case "mount":
                return await MountRemoteVolumeAsync(args, targetDevice, cancellationToken);
            case "unmount":
                return await UnmountRemoteVolumeAsync(args, targetDevice, cancellationToken);
            case "eject":
                return await EjectRemoteVolumeAsync(args, targetDevice, cancellationToken);
            case "input":
                return await RunInputCommandAsync(args, targetDevice, cancellationToken);
            case "-h":
            case "--help":
            case "help":
                WriteUsage(output);
                return 0;
            default:
                error.WriteLine($"Unknown command: {args[0]}");
                WriteUsage(error);
                return 2;
        }
    }

    private async Task<int> RunInputCommandAsync(
        string[] args, string? targetDevice, CancellationToken cancellationToken)
    {
        if (args.Length > 2)
        {
            error.WriteLine("Usage: xas input [device-id]");
            return 2;
        }
        var selectedDevice = ResolveTarget(targetDevice, args.Skip(1).ToArray());
        return await client.RunInputAsync(selectedDevice, cancellationToken);
    }

    private async Task<int> ListDevicesAsync(CancellationToken ct)
    {
        var devices = await client.ListDevicesAsync(ct);
        foreach (var device in devices)
        {
            var capabilities = string.Join(
                ',', device.Capabilities.Select(capability =>
                    $"{capability.Capability}:v{capability.Version}"));
            var lastSeen = device.LastSeen?.ToString(
                "u", System.Globalization.CultureInfo.InvariantCulture) ?? "never";
            var latency = device.RoundTripMilliseconds is { } rtt ? $"{rtt:0.0}ms" : "unknown";
            var deviceSummary = string.Join('\t',
                device.DeviceId,
                device.Name,
                $"{device.Os} {device.Architecture}",
                device.Online ? "online" : "offline",
                $"last-seen={lastSeen}",
                $"rtt={latency}",
                $"capabilities={capabilities}");
            output.WriteLine(deviceSummary);
        }
        if (devices.Count == 0) output.WriteLine("No devices found.");
        return 0;
    }

    private async Task<int> ShowInfoAsync(string? id, CancellationToken ct)
    {
        var device = await client.GetDeviceInfoAsync(id, ct);
        if (device is null)
        {
            error.WriteLine(id is null ? "No default device is configured." : $"Device not found: {id}");
            return 1;
        }
        output.WriteLine($"Id: {device.DeviceId}");
        output.WriteLine($"Name: {device.Name}");
        output.WriteLine($"OS: {device.Os}");
        output.WriteLine($"Architecture: {device.Architecture}");
        output.WriteLine("Capabilities:");
        foreach (var capability in device.Capabilities)
            output.WriteLine($"  {capability.Capability} v{capability.Version}");
        return 0;
    }

    private async Task<int> SetDefaultAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 2)
        {
            error.WriteLine("Usage: xas default <device-id>");
            return 2;
        }
        await client.SetDefaultDeviceAsync(args[1], ct);
        output.WriteLine($"Default device set to {args[1]}.");
        return 0;
    }

    private async Task<int> PingAsync(string? id, CancellationToken ct)
    {
        var ok = await client.PingAsync(id, ct);
        output.WriteLine(ok ? "pong" : "Device did not respond.");
        return ok ? 0 : 1;
    }

    private async Task<int> RunCommandAsync(
        string[] args, string? deviceId, bool elevated, CancellationToken ct)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: xas -c <command>");
            return 2;
        }
        var request = new ShellRequest(
            ShellMode.Command, args[1], null, Array.Empty<string>(), Elevated: elevated);
        return await client.RunShellAsync(request, deviceId, ct);
    }

    private async Task<int> RunExecutableAsync(
        string[] args, string? deviceId, bool elevated, CancellationToken ct)
    {
        if (args.Length > 1 && args[1] == "--sudo")
        {
            elevated = true;
            args = [args[0], .. args.Skip(2)];
        }
        var executableIndex = args.Length > 1 && args[1] == "--" ? 2 : 1;
        if (args.Length <= executableIndex)
        {
            error.WriteLine("Usage: xas exec [--] <executable> [arguments...]");
            return 2;
        }
        var request = new ShellRequest(
            ShellMode.Exec,
            null,
            args[executableIndex],
            args.Skip(executableIndex + 1).ToArray(),
            Elevated: elevated);
        return await client.RunShellAsync(request, deviceId, ct);
    }

    private async Task<int> CopyAsync(string[] args, string? targetDevice, CancellationToken ct)
    {
        if (targetDevice is not null)
        {
            error.WriteLine("Specify the remote device in the copy operand, for example laptop:~/file.");
            return 2;
        }
        var recursive = false;
        var overwrite = false;
        var index = 1;
        while (index < args.Length && args[index].StartsWith("-", StringComparison.Ordinal))
        {
            if (args[index] == "--") { index++; break; }
            if (args[index] is "-r" or "--recursive") recursive = true;
            else if (args[index] is "-f" or "--force") overwrite = true;
            else { error.WriteLine($"Unknown copy option: {args[index]}"); return 2; }
            index++;
        }
        if (args.Length - index != 2)
        {
            error.WriteLine("Usage: xas cp [-r] [-f] <source> <destination>");
            return 2;
        }
        return await client.CopyAsync(args[index], args[index + 1], recursive, overwrite, ct);
    }

    private async Task<int> ClipboardAsync(
        string[] args, string? targetDevice, CancellationToken ct)
    {
        if (args.Length is < 2 or > 3 || args[1] is not ("push" or "pull" or "sync"))
        {
            error.WriteLine("Usage: xas clipboard push|pull|sync [device-id]");
            return 2;
        }
        if (args[1] == "sync")
        {
            var target = ResolveTarget(targetDevice, args.Skip(2).ToArray());
            return await client.WatchClipboardAsync(target, ct);
        }
        var selectedDevice = ResolveTarget(targetDevice, args.Skip(2).ToArray());
        return await client.SyncClipboardAsync(args[1] == "push", selectedDevice, ct);
    }

    private async Task<int> ListRemoteVolumesAsync(string? targetDevice, CancellationToken ct)
    {
        var volumes = await client.ListRemoteVolumesAsync(targetDevice, ct);
        if (volumes.Count == 0)
        {
            output.WriteLine("No remote volumes found.");
            return 0;
        }
        foreach (var volume in volumes)
        {
            var state = GetVolumeState(volume);
            var volumeSummary = string.Join('\t',
                volume.Name,
                volume.Kind,
                volume.ReadOnly ? "ro" : "rw",
                FormatBytes(volume.TotalBytes),
                volume.FileSystem ?? "unknown",
                state,
                volume.VolumeId);
            output.WriteLine(volumeSummary);
        }
        return 0;
    }

    private async Task<int> ListRemoteMountsAsync(string? targetDevice, CancellationToken ct)
    {
        var mounts = await client.ListRemoteMountsAsync(targetDevice, ct);
        if (mounts.Count == 0)
        {
            output.WriteLine("No remote volumes are mounted.");
            return 0;
        }
        foreach (var mount in mounts)
        {
            var mountSummary = string.Join('\t',
                mount.MountedAt ?? "?",
                mount.Name,
                mount.DeviceName,
                mount.ReadOnly ? "ro" : "rw",
                FormatBytes(mount.TotalBytes),
                mount.VolumeId);
            output.WriteLine(mountSummary);
        }
        return 0;
    }

    private async Task<int> MountRemoteVolumeAsync(
        string[] args, string? targetDevice, CancellationToken ct)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: xas [-d <device>] mount <volume-name-or-id>");
            return 2;
        }
        var mounted = await client.MountRemoteVolumeAsync(targetDevice, args[1], ct);
        var mountPoint = mounted.MountedAt ?? "native mount";
        output.WriteLine($"Mounted {mounted.Name} from {mounted.DeviceName} at {mountPoint}.");
        return 0;
    }

    private async Task<int> UnmountRemoteVolumeAsync(
        string[] args, string? targetDevice, CancellationToken ct)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: xas [-d <device>] unmount <volume-name-or-id>");
            return 2;
        }
        var wasMounted = await client.UnmountRemoteVolumeAsync(targetDevice, args[1], ct);
        var status = wasMounted
            ? $"Unmounted {args[1]}; automatic remount is suppressed " +
                "until it disappears or you mount it again."
            : $"{args[1]} was not mounted; automatic mounting is now suppressed " +
                "until it disappears or you mount it again.";
        output.WriteLine(status);
        return 0;
    }

    private async Task<int> EjectRemoteVolumeAsync(
        string[] args, string? targetDevice, CancellationToken ct)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: xas [-d <device>] eject <volume-name-or-id>");
            return 2;
        }
        await client.EjectRemoteVolumeAsync(targetDevice, args[1], ct);
        output.WriteLine($"Safely ejected {args[1]} on the remote device.");
        return 0;
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null || bytes < 0) return "unknown";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static string GetVolumeState(LocalRemoteVolumeInfo volume)
    {
        if (volume.MountedAt is { Length: > 0 } mountPoint)
            return $"mounted={mountPoint}";
        if (volume.AutoMountSuppressed)
            return "unmounted (suppressed)";
        return "available";
    }

    private static string? ResolveTarget(string? targetDevice, string[] positional)
    {
        if (positional.Length > 1)
            throw new XasClientException("Expected at most one device id.");
        if (targetDevice is not null && positional.Length == 1)
            throw new XasClientException(
                "Specify the target once, either with -d or as the command argument.");
        return targetDevice ?? positional.FirstOrDefault();
    }

    private static void WriteUsage(TextWriter writer)
    {
        string[] lines =
        [
            "Usage: xas <command>",
            "  -d <device-id> <command>  Target a specific device",
            "  devices                 List known devices",
            "  info [device-id]        Show device information (default device if omitted)",
            "  default <device-id>     Select the default device",
            "  ping [device-id]        Check device connectivity",
            "  [--sudo] -c <command>  Run a shell command (optionally elevated)",
            "  exec [--sudo] [--] <exe> [args]  Run an executable (optionally elevated)",
            "  cp [-r] [-f] <src> <dst>  Copy files to or from a paired device " +
                "(:path uses the default device)",
            "  clipboard push|pull|sync [id]  Transfer or continuously sync plain text",
            "  volumes [device-id]      List volumes exported by a remote device",
            "  mounts [device-id]       List currently mounted remote volumes",
            "  mount <volume>           Mount a remote volume natively (use -d to select device)",
            "  unmount <volume>         Unmount and suppress automatic remount until reinsertion",
            "  eject <volume>           Safely unmount/eject removable media on the remote device",
            "  input [device-id]       Capture Windows input manually (Ctrl+Alt+Esc releases)",
            "  display                 Report virtual display driver and handoff state (Windows)",
            "  (no arguments)          Open an interactive shell (requires PTY support)",
            "  identity                Show local device ID and fingerprint",
            "  ui                      Open the local xas configuration UI",
            "  pair [name|short-id] [--kvm|--trust-only]  Pair a discovered nearby device",
            "  pair-manual <id> <fp> <host> [port]       Recovery/manual pairing",
            "  peers                   List locally approved peers",
            "  allow|deny <id> <capability>        Set local peer permission",
            "  revoke <id>             Remove local trust, permissions, and endpoint",
            "  endpoint <id> <host> [port]         Update a paired peer address"
        ];
        writer.WriteLine(string.Join(Environment.NewLine, lines));
    }
}

/// <summary>Raised by client implementations when transport or daemon operations fail.</summary>
public sealed class XasClientException(string message) : Exception(message);
