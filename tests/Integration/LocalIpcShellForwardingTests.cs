using System.Collections.Concurrent;
using Xas.Core;
using Xas.Core.Protocol;
using Xas.Core.Services;
using Xas.Daemon.LocalIpc;
using Xas.Daemon.Sessions;

namespace Xas.Tests;

public static class LocalIpcShellForwardingTests
{
    public static async Task RunAsync()
    {
        var forwarded = new ConcurrentQueue<ProtocolMessage>();
        await using var streams = new LocalIpcServer.BoundShellStreams((message, _) =>
        {
            forwarded.Enqueue(message);
            return ValueTask.CompletedTask;
        });

        await streams.BeginOpenAsync();
        var earlyExec = CreateStreamMessage(7, ShellExecWire.Stdout, "before"u8.ToArray());
        await streams.OnMessageAsync(earlyExec);
        await streams.OnMessageAsync(
            CreateStreamMessage(7, "shell.output", "foreign"u8.ToArray()));
        await streams.CompleteOpenAsync(
            7, exec: true, closeMethod: ShellExecWire.Close);
        await streams.EndOpenAsync();
        var liveExec = CreateStreamMessage(7, ShellExecWire.Stdout, "after"u8.ToArray());
        await streams.OnMessageAsync(liveExec);
        await streams.OnMessageAsync(new ProtocolMessage(MessageKind.Event, 0, 7,
            "shell.exit", ShellExecWire.EncodeExitCode(4)));

        await streams.BeginOpenAsync();
        await streams.CompleteOpenAsync(7, exec: false, closeMethod: "shell.close");
        await streams.EndOpenAsync();
        var liveInteractive = CreateStreamMessage(
            7, "shell.output", "interactive"u8.ToArray());
        var secondLiveExec = CreateStreamMessage(
            7, ShellExecWire.Stdout, "exec-again"u8.ToArray());
        foreach (var liveMessage in new[] { liveInteractive, secondLiveExec })
        {
            await streams.OnMessageAsync(liveMessage);
        }
        var expected = new[] { earlyExec, liveExec, liveInteractive, secondLiveExec };
        Assert(forwarded.Count == expected.Length && forwarded.SequenceEqual(expected),
            "Bound IPC shell forwarding crossed shell families, or reordered buffered and live output.");

        var disconnected = new LocalIpcServer.BoundShellStreams((_, _) =>
            ValueTask.FromException(new IOException("The local IPC client disconnected.")));
        await disconnected.BeginOpenAsync();
        await disconnected.CompleteOpenAsync(9, exec: true, closeMethod: ShellExecWire.Close);
        await disconnected.EndOpenAsync();
        await disconnected.OnMessageAsync(new ProtocolMessage(MessageKind.Event, 0, 9,
            ShellExecWire.Exit, ShellExecWire.EncodeExitCode(0)));
        await disconnected.CloseAllAsync(new PeerSession("device", "test"));
        await disconnected.DisposeAsync();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ProtocolMessage CreateStreamMessage(
        uint streamId,
        string method,
        byte[] payload) =>
        new(MessageKind.StreamData, 0, streamId, method, payload);
}
