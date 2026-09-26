using Xas.Core;
using Xas.Core.LocalIpc;

namespace Xas.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var localResult = await LocalSetupCommands.TryRunAsync(args);
            if (localResult is not null) return localResult.Value;
            using var client = new LocalDaemonClient(
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(), Console.OpenStandardError());
            return await new XasCommandLine(client, Console.Out, Console.Error).RunAsync(args);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or
            IOException or NotSupportedException or UnauthorizedAccessException or TimeoutException or
            System.ComponentModel.Win32Exception or System.Security.Authentication.AuthenticationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
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
    Task<IReadOnlyList<LocalRemoteVolumeInfo>> ListRemoteVolumesAsync(string? deviceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LocalRemoteVolumeInfo>> ListRemoteMountsAsync(string? deviceId, CancellationToken cancellationToken);
    Task<LocalRemoteVolumeInfo> MountRemoteVolumeAsync(string? deviceId, string volume, CancellationToken cancellationToken);
    Task<bool> UnmountRemoteVolumeAsync(string? deviceId, string volume, CancellationToken cancellationToken);
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
            try { return await client.RunInteractiveAsync(targetDevice, elevated, cancellationToken); }
            catch (XasClientException ex) { error.WriteLine(ex.Message); return 1; }
        }

        if (elevated && args[0].ToLowerInvariant() is not ("-c" or "exec"))
        {
            error.WriteLine("--sudo is only available for shell commands and interactive shells.");
            return 2;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "devices":
                    return await ListDevicesAsync(cancellationToken);
                case "info":
                    return await ShowInfoAsync(ResolveTarget(targetDevice, args.Skip(1).ToArray()), cancellationToken);
                case "default":
                    return await SetDefaultAsync(args, cancellationToken);
                case "ping":
                    return await PingAsync(ResolveTarget(targetDevice, args.Skip(1).ToArray()), cancellationToken);
                case "-c":
                    return await RunCommandAsync(args, targetDevice, elevated, cancellationToken);
                case "exec":
                    return await RunExecutableAsync(args, targetDevice, elevated, cancellationToken);
                case "cp":
                    return await CopyAsync(args, targetDevice, cancellationToken);
                case "clipboard":
                    return await ClipboardAsync(args, targetDevice, cancellationToken);
                case "volumes":
                    return await ListRemoteVolumesAsync(ResolveTarget(targetDevice, args.Skip(1).ToArray()), cancellationToken);
                case "mounts":
                    return await ListRemoteMountsAsync(ResolveTarget(targetDevice, args.Skip(1).ToArray()), cancellationToken);
                case "mount":
                    return await MountRemoteVolumeAsync(args, targetDevice, cancellationToken);
                case "unmount":
                    return await UnmountRemoteVolumeAsync(args, targetDevice, cancellationToken);
                case "eject":
                    return await EjectRemoteVolumeAsync(args, targetDevice, cancellationToken);
                case "input":
                    if (args.Length > 2) { error.WriteLine("Usage: xas input [device-id]"); return 2; }
                    return await client.RunInputAsync(ResolveTarget(targetDevice, args.Skip(1).ToArray()), cancellationToken);
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
        catch (XasClientException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private async Task<int> ListDevicesAsync(CancellationToken ct)
    {
        var devices = await client.ListDevicesAsync(ct);
        foreach (var device in devices)
        {
            var capabilities = string.Join(',', device.Capabilities.Select(c => $"{c.Capability}:v{c.Version}"));
            var lastSeen = device.LastSeen?.ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "never";
            var latency = device.RoundTripMilliseconds is { } rtt ? $"{rtt:0.0}ms" : "unknown";
            output.WriteLine($"{device.DeviceId}\t{device.Name}\t{device.Os} {device.Architecture}\t{(device.Online ? "online" : "offline")}\tlast-seen={lastSeen}\trtt={latency}\tcapabilities={capabilities}");
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

    private async Task<int> RunCommandAsync(string[] args, string? deviceId, bool elevated, CancellationToken ct)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: xas -c <command>");
            return 2;
        }
        return await client.RunShellAsync(new ShellRequest(ShellMode.Command, args[1], null, Array.Empty<string>(), Elevated: elevated), deviceId, ct);
    }

    private async Task<int> RunExecutableAsync(string[] args, string? deviceId, bool elevated, CancellationToken ct)
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
        return await client.RunShellAsync(new ShellRequest(ShellMode.Exec, null, args[executableIndex], args.Skip(executableIndex + 1).ToArray(), Elevated: elevated), deviceId, ct);
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

    private async Task<int> ClipboardAsync(string[] args, string? targetDevice, CancellationToken ct)
    {
        if (args.Length is < 2 or > 3 || args[1] is not ("push" or "pull" or "sync"))
        {
            error.WriteLine("Usage: xas clipboard push|pull|sync [device-id]");
            return 2;
        }
        if (args[1] == "sync")
            return await client.WatchClipboardAsync(ResolveTarget(targetDevice, args.Skip(2).ToArray()), ct);
        return await client.SyncClipboardAsync(args[1] == "push",
            ResolveTarget(targetDevice, args.Skip(2).ToArray()), ct);
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
            var state = volume.MountedAt is { Length: > 0 } mounted
                ? $"mounted={mounted}"
                : volume.AutoMountSuppressed ? "unmounted (suppressed)" : "available";
            output.WriteLine($"{volume.Name}\t{volume.Kind}\t{(volume.ReadOnly ? "ro" : "rw")}\t{FormatBytes(volume.TotalBytes)}\t{volume.FileSystem ?? "unknown"}\t{state}\t{volume.VolumeId}");
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
            output.WriteLine($"{mount.MountedAt ?? "?"}\t{mount.Name}\t{mount.DeviceName}\t{(mount.ReadOnly ? "ro" : "rw")}\t{FormatBytes(mount.TotalBytes)}\t{mount.VolumeId}");
        return 0;
    }

    private async Task<int> MountRemoteVolumeAsync(string[] args, string? targetDevice, CancellationToken ct)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: xas [-d <device>] mount <volume-name-or-id>");
            return 2;
        }
        var mounted = await client.MountRemoteVolumeAsync(targetDevice, args[1], ct);
        output.WriteLine($"Mounted {mounted.Name} from {mounted.DeviceName} at {mounted.MountedAt ?? "native mount"}.");
        return 0;
    }

    private async Task<int> UnmountRemoteVolumeAsync(string[] args, string? targetDevice, CancellationToken ct)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: xas [-d <device>] unmount <volume-name-or-id>");
            return 2;
        }
        var wasMounted = await client.UnmountRemoteVolumeAsync(targetDevice, args[1], ct);
        output.WriteLine(wasMounted
            ? $"Unmounted {args[1]}; automatic remount is suppressed until it disappears or you mount it again."
            : $"{args[1]} was not mounted; automatic mounting is now suppressed until it disappears or you mount it again.");
        return 0;
    }

    private async Task<int> EjectRemoteVolumeAsync(string[] args, string? targetDevice, CancellationToken ct)
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

    private static string? ResolveTarget(string? targetDevice, string[] positional)
    {
        if (positional.Length > 1)
            throw new XasClientException("Expected at most one device id.");
        if (targetDevice is not null && positional.Length == 1)
            throw new XasClientException("Specify the target once, either with -d or as the command argument.");
        return targetDevice ?? positional.FirstOrDefault();
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: xas <command>");
        writer.WriteLine("  -d <device-id> <command>  Target a specific device");
        writer.WriteLine("  devices                 List known devices");
        writer.WriteLine("  info [device-id]        Show device information (default device if omitted)");
        writer.WriteLine("  default <device-id>     Select the default device");
        writer.WriteLine("  ping [device-id]        Check device connectivity");
        writer.WriteLine("  [--sudo] -c <command>  Run a shell command (optionally elevated)");
        writer.WriteLine("  exec [--sudo] [--] <exe> [args]  Run an executable (optionally elevated)");
        writer.WriteLine("  cp [-r] [-f] <src> <dst>  Copy files to or from a paired device (:path uses the default device)");
        writer.WriteLine("  clipboard push|pull|sync [id]  Transfer or continuously sync plain text");
        writer.WriteLine("  volumes [device-id]      List volumes exported by a remote device");
        writer.WriteLine("  mounts [device-id]       List currently mounted remote volumes");
        writer.WriteLine("  mount <volume>           Mount a remote volume natively (use -d to select device)");
        writer.WriteLine("  unmount <volume>         Unmount and suppress automatic remount until reinsertion");
        writer.WriteLine("  eject <volume>           Safely unmount/eject removable media on the remote device");
        writer.WriteLine("  input [device-id]       Capture Windows input manually (Ctrl+Alt+Esc releases)");
        writer.WriteLine("  display                 Report virtual display driver and handoff state (Windows)");
        writer.WriteLine("  (no arguments)          Open an interactive shell (requires PTY support)");
        writer.WriteLine("  identity                Show local device ID and fingerprint");
        writer.WriteLine("  ui                      Open the local xas configuration UI");
        writer.WriteLine("  pair [name|short-id] [--kvm|--trust-only]  Pair a discovered nearby device");
        writer.WriteLine("  pair-manual <id> <fp> <host> [port]       Recovery/manual pairing");
        writer.WriteLine("  peers                   List locally approved peers");
        writer.WriteLine("  allow|deny <id> <capability>        Set local peer permission");
        writer.WriteLine("  revoke <id>             Remove local trust, permissions, and endpoint");
        writer.WriteLine("  endpoint <id> <host> [port]         Update a paired peer address");
    }
}

/// <summary>Raised by client implementations when transport or daemon operations fail.</summary>
public sealed class XasClientException(string message) : Exception(message);
