using Xas.Cli;
using Xas.Core;

namespace Xas.Tests;

public static class CliTests
{
    public static async Task RunAsync()
    {
        await CommandPreservesOneExactArgumentAndTarget();
        await ExecPassesExecutableArgumentsAndTarget();
        await InfoAndPingUseExplicitTarget();
        await InvalidTargetAndExtraCommandArgumentsAreRejected();
        await InteractiveDefaultUsesClient();
    }

    private static async Task CommandPreservesOneExactArgumentAndTarget()
    {
        var client = new FakeClient();
        var cli = CreateCli(client);
        var code = await cli.RunAsync(["-d", "workstation", "-c", "printf  a  b"]);
        Equal(7, code);
        Equal("workstation", client.ShellDeviceId);
        Equal("printf  a  b", client.ShellRequest?.Command);
        Equal(ShellMode.Command, client.ShellRequest?.Mode);
    }

    private static async Task ExecPassesExecutableArgumentsAndTarget()
    {
        var client = new FakeClient();
        var cli = CreateCli(client);
        var code = await cli.RunAsync(["-d", "linux-box", "exec", "--", "/usr/bin/printf", "%s", "hello world"]);
        Equal(7, code);
        Equal("linux-box", client.ShellDeviceId);
        Equal("/usr/bin/printf", client.ShellRequest?.Executable);
        SequenceEqual(new[] { "%s", "hello world" }, client.ShellRequest?.Arguments);
    }

    private static async Task InfoAndPingUseExplicitTarget()
    {
        var client = new FakeClient();
        var cli = CreateCli(client);
        Equal(0, await cli.RunAsync(["-d", "device-a", "info"]));
        Equal("device-a", client.InfoDeviceId);
        Equal(0, await cli.RunAsync(["-d", "device-b", "ping"]));
        Equal("device-b", client.PingDeviceId);
    }

    private static async Task InvalidTargetAndExtraCommandArgumentsAreRejected()
    {
        var client = new FakeClient();
        var cli = CreateCli(client);
        Equal(2, await cli.RunAsync(["-d", "-c", "hello"]));
        Equal(2, await cli.RunAsync(["-c", "hello", "unexpected"]));
        Equal<ShellRequest?>(null, client.ShellRequest);
    }

    private static async Task InteractiveDefaultUsesClient()
    {
        var client = new FakeClient();
        var cli = CreateCli(client);
        Equal(9, await cli.RunAsync([]));
        Equal<string?>(null, client.InteractiveDeviceId);
        Equal(9, await cli.RunAsync(["-d", "device-c"]));
        Equal("device-c", client.InteractiveDeviceId);
    }

    private static XasCommandLine CreateCli(FakeClient client) => new(client, TextWriter.Null, TextWriter.Null);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T>? actual)
    {
        if (actual is null || !expected.SequenceEqual(actual))
            throw new InvalidOperationException("Sequences were not equal.");
    }

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected text to contain '{expected}'.");
    }

    private sealed class FakeClient : IXasClient
    {
        public ShellRequest? ShellRequest { get; private set; }
        public string? ShellDeviceId { get; private set; }
        public string? InfoDeviceId { get; private set; }
        public string? PingDeviceId { get; private set; }
        public string? InteractiveDeviceId { get; private set; }

        public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeviceInfo>>([]);
        public Task<DeviceInfo?> GetDeviceInfoAsync(string? deviceId, CancellationToken cancellationToken)
        {
            InfoDeviceId = deviceId;
            return Task.FromResult<DeviceInfo?>(new DeviceInfo(deviceId ?? "default", "Test device", "Linux", "x64", []));
        }
        public Task SetDefaultDeviceAsync(string deviceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> PingAsync(string? deviceId, CancellationToken cancellationToken)
        {
            PingDeviceId = deviceId;
            return Task.FromResult(true);
        }
        public Task<int> RunShellAsync(ShellRequest request, string? deviceId, CancellationToken cancellationToken)
        {
            ShellRequest = request;
            ShellDeviceId = deviceId;
            return Task.FromResult(7);
        }
        public Task<int> RunInteractiveAsync(string? deviceId, CancellationToken cancellationToken)
        {
            InteractiveDeviceId = deviceId;
            return Task.FromResult(9);
        }
    }
}
