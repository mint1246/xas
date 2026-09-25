using Xas.Core;

namespace Xas.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var localResult = await LocalSetupCommands.TryRunAsync(args);
            if (localResult is not null) return localResult.Value;
            using var client = new RemoteXasClient(Console.OpenStandardOutput(), Console.OpenStandardError());
            return await new XasCommandLine(client, Console.Out, Console.Error).RunAsync(args);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

/// <summary>Transport boundary for the command line. A daemon transport can implement this without changing parsing.</summary>
public interface IXasClient
{
    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken);
    Task<DeviceInfo?> GetDeviceInfoAsync(string? deviceId, CancellationToken cancellationToken);
    Task SetDefaultDeviceAsync(string deviceId, CancellationToken cancellationToken);
    Task<bool> PingAsync(string? deviceId, CancellationToken cancellationToken);
    Task<int> RunShellAsync(ShellRequest request, string? deviceId, CancellationToken cancellationToken);
}

public sealed class XasCommandLine(IXasClient client, TextWriter output, TextWriter error)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        string? targetDevice = null;
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

        if (args.Length == 0)
            return UnsupportedInteractive();

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
                    return await RunCommandAsync(args, targetDevice, cancellationToken);
                case "exec":
                    return await RunExecutableAsync(args, targetDevice, cancellationToken);
                case "--sudo":
                case "--admin":
                    error.WriteLine("Privileged remote shells are unavailable: no platform privilege broker is installed.");
                    return 3;
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
            output.WriteLine($"{device.DeviceId}\t{device.Name}\t{device.Os} {device.Architecture}");
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

    private async Task<int> RunCommandAsync(string[] args, string? deviceId, CancellationToken ct)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: xas -c <command>");
            return 2;
        }
        return await client.RunShellAsync(new ShellRequest(ShellMode.Command, args[1], null, Array.Empty<string>()), deviceId, ct);
    }

    private async Task<int> RunExecutableAsync(string[] args, string? deviceId, CancellationToken ct)
    {
        var executableIndex = args.Length > 1 && args[1] == "--" ? 2 : 1;
        if (args.Length <= executableIndex)
        {
            error.WriteLine("Usage: xas exec [--] <executable> [arguments...]");
            return 2;
        }
        return await client.RunShellAsync(new ShellRequest(ShellMode.Exec, null, args[executableIndex], args.Skip(executableIndex + 1).ToArray()), deviceId, ct);
    }

    private static string? ResolveTarget(string? targetDevice, string[] positional)
    {
        if (positional.Length > 1)
            throw new XasClientException("Expected at most one device id.");
        if (targetDevice is not null && positional.Length == 1)
            throw new XasClientException("Specify the target once, either with -d or as the command argument.");
        return targetDevice ?? positional.FirstOrDefault();
    }

    private int UnsupportedInteractive()
    {
        error.WriteLine("Interactive shell is unavailable: this CLI has no PTY streaming transport yet. Use 'xas -c <command>' or 'xas exec <executable> [arguments...]' when connected to a compatible daemon.");
        return 3;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: xas <command>");
        writer.WriteLine("  -d <device-id> <command>  Target a specific device");
        writer.WriteLine("  devices                 List known devices");
        writer.WriteLine("  info [device-id]        Show device information (default device if omitted)");
        writer.WriteLine("  default <device-id>     Select the default device");
        writer.WriteLine("  ping [device-id]        Check device connectivity");
        writer.WriteLine("  -c <command>            Run a shell command on the selected/default device");
        writer.WriteLine("  exec [--] <exe> [args]  Run an executable on the selected/default device");
        writer.WriteLine("  (no arguments)          Open an interactive shell (requires PTY support)");
        writer.WriteLine("  identity                Show local device ID and fingerprint");
        writer.WriteLine("  pair <id> <fp> <host> [port] [name]  Approve a peer locally");
        writer.WriteLine("  peers                   List locally approved peers");
        writer.WriteLine("  allow|deny <id> <capability>        Set local peer permission");
        writer.WriteLine("  revoke <id>             Remove local trust, permissions, and endpoint");
        writer.WriteLine("  endpoint <id> <host> [port]         Update a paired peer address");
    }
}

/// <summary>Raised by client implementations when transport or daemon operations fail.</summary>
public sealed class XasClientException(string message) : Exception(message);
