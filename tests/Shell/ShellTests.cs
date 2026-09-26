using System.Text;
using Xas.Core;
using Xas.Daemon.Shell;

namespace Xas.Tests;

public static class ShellTests
{
    public static async Task RunAsync()
    {
        await ExecPreservesArgumentsAsync();
        await CommandUsesHostShellAndReturnsStreamsAndExitCodeAsync();
        await ExecForwardsStandardInputAsync();
        await RejectsInteractiveAndElevatedRequestsAsync();
        await CancellationStopsTheChildAsync();
    }

    private static async Task ExecPreservesArgumentsAsync()
    {
        var request = new ShellRequest(ShellMode.Exec, null, "dotnet",
            [typeof(ShellTests).Assembly.Location, "--echo-args", "alpha", "two words", "quote'and\"double", ""]);
        const string expected = "alpha|two words|quote'and\"double|";

        var result = await Run(request);
        Equal(0, result.ExitCode, "Exec should succeed.");
        Equal(expected, result.Stdout, "Exec argument boundaries or contents changed.");
        Equal(string.Empty, result.Stderr, "Exec unexpectedly wrote to stderr.");
    }

    private static async Task CommandUsesHostShellAndReturnsStreamsAndExitCodeAsync()
    {
        var command = OperatingSystem.IsWindows()
            ? "echo shell-out & echo shell-err 1>&2 & exit /b 23"
            : "printf shell-out; printf shell-err >&2; exit 23";
        var result = await Run(new ShellRequest(ShellMode.Command, command, null, []));
        Equal(23, result.ExitCode, "Command exit code was not preserved.");
        Contains("shell-out", result.Stdout, "Command stdout was not captured.");
        Contains("shell-err", result.Stderr, "Command stderr was not captured.");
    }

    private static async Task ExecForwardsStandardInputAsync()
    {
        var request = new ShellRequest(ShellMode.Exec, null, "dotnet",
            [typeof(ShellTests).Assembly.Location, "--echo-stdin"]);
        var inputText = "stdin payload with spaces\nsecond line\n";
        var result = await Run(request, Encoding.UTF8.GetBytes(inputText));
        Equal(0, result.ExitCode, "Input echo process should succeed.");
        Equal(inputText, result.Stdout, "Standard input was not forwarded unchanged.");
    }

    private static async Task RejectsInteractiveAndElevatedRequestsAsync()
    {
        var backend = new ProcessShellBackend();
        Assert(!backend.SupportsInteractive, "The process backend must not advertise interactive support.");
        await Throws<NotSupportedException>(() => backend.RunAsync(
            new ShellRequest(ShellMode.Interactive, null, null, []), Stream.Null, Stream.Null, Stream.Null, default));
        await Throws<NotSupportedException>(() => backend.RunAsync(
            new ShellRequest(ShellMode.Interactive, null, null, [], Elevated: true), Stream.Null, Stream.Null, Stream.Null, default));
        if (!OperatingSystem.IsLinux())
            await Throws<NotSupportedException>(() => backend.RunAsync(
                new ShellRequest(ShellMode.Exec, null, "echo", [], Elevated: true), Stream.Null, Stream.Null, Stream.Null, default));
    }

    private static async Task CancellationStopsTheChildAsync()
    {
        var request = OperatingSystem.IsWindows()
            ? new ShellRequest(ShellMode.Command, "ping -n 30 127.0.0.1 > nul", null, [])
            : new ShellRequest(ShellMode.Command, "sleep 30", null, []);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await Run(request, cancellationToken: cancellation.Token);
            throw new Exception("A cancelled shell process completed normally.");
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> Run(
        ShellRequest request, byte[]? input = null, CancellationToken cancellationToken = default)
    {
        using var stdin = new MemoryStream(input ?? []);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var exitCode = await new ProcessShellBackend().RunAsync(request, stdin, stdout, stderr, cancellationToken);
        return (exitCode, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    private static async Task Throws<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{message} Expected '{expected}', got '{actual}'.");
    }

    private static void Contains(string expected, string actual, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new Exception($"{message} Actual output: '{actual}'.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
