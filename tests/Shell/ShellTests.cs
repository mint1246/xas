using System.Text;
using Xas.Core;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Services;
using Xas.Daemon.Shell;

namespace Xas.Tests;

public static class ShellTests
{
    public static async Task RunAsync()
    {
        await ExecPreservesArgumentsAsync();
        await CommandUsesHostShellAndReturnsStreamsAndExitCodeAsync();
        await ExecForwardsStandardInputAsync();
        await ShortLivedCommandDoesNotWaitForOpenStandardInputAsync();
        await CompletedCommandSessionsAreRemovedAndCloseIsIdempotentAsync();
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

    private static async Task ShortLivedCommandDoesNotWaitForOpenStandardInputAsync()
    {
        var command = OperatingSystem.IsWindows() ? "echo finished" : "printf finished";
        using var stdin = new NeverEndingInputStream();
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var run = new ProcessShellBackend().RunAsync(
            new ShellRequest(ShellMode.Command, command, null, []), stdin, stdout, stderr, default);
        try
        {
            var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, exitCode, "Short-lived command should succeed with an open input stream.");
            Equal(OperatingSystem.IsWindows() ? "finished\r\n" : "finished",
                Encoding.UTF8.GetString(stdout.ToArray()), "Output was lost while stopping input forwarding.");
        }
        catch (TimeoutException)
        {
            stdin.Stop();
            try { await run; } catch (OperationCanceledException) { }
            throw new Exception("The shell waited for stdin to close after the child exited.");
        }
    }

    private static async Task CompletedCommandSessionsAreRemovedAndCloseIsIdempotentAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xas-shell-manager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var permissions = new PeerPermissionStore(directory);
            permissions.SetAllowed("test-peer", Capability.Shell, true);
            var exitSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var manager = new StreamingCommandManager("test-peer", permissions, (message, _) =>
            {
                if (message.Method == ShellExecWire.Exit) exitSent.TrySetResult();
                return ValueTask.CompletedTask;
            }, new ImmediateShellBackend());

            var open = new ProtocolMessage(MessageKind.Request, 1, 0, ShellExecWire.Open,
                ShellWire.EncodeRequest(new ShellRequest(ShellMode.Command, "ignored", null, [])));
            var response = await manager.HandleRequestAsync(open, default);
            var id = ShellExecWire.DecodeSessionId(response.Payload);
            await exitSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => manager.ActiveSessionCount == 0);

            var close = new ProtocolMessage(MessageKind.Request, 2, 0, ShellExecWire.Close,
                ShellExecWire.EncodeSessionId(id));
            Equal(MessageKind.Response, (await manager.HandleRequestAsync(close, default)).Kind,
                "Closing an already completed session should succeed.");
            Equal(MessageKind.Response, (await manager.HandleRequestAsync(close with { RequestId = 3 }, default)).Kind,
                "Closing the same session twice should be harmless.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert(predicate(), "The completed command session remained registered.");
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

    private sealed class NeverEndingInputStream : Stream
    {
        private readonly CancellationTokenSource _stop = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public void Stop() => _stop.Cancel();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            WaitAsync(cancellationToken);
        private async ValueTask<int> WaitAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _stop.Dispose(); base.Dispose(disposing); }
    }

    private sealed class ImmediateShellBackend : IShellBackend
    {
        public bool SupportsInteractive => false;
        public Task<int> RunAsync(ShellRequest request, Stream stdin, Stream stdout, Stream stderr,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
