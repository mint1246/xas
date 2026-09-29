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
        var earlyExec = new ProtocolMessage(MessageKind.StreamData, 0, 7, ShellExecWire.Stdout, "before"u8.ToArray());
        await streams.OnMessageAsync(earlyExec);
        await streams.OnMessageAsync(new ProtocolMessage(MessageKind.StreamData, 0, 7,
            "shell.output", "foreign"u8.ToArray()));
        await streams.CompleteOpenAsync(7, exec: true, closeMethod: ShellExecWire.Close);
        await streams.EndOpenAsync();
        var liveExec = new ProtocolMessage(MessageKind.StreamData, 0, 7, ShellExecWire.Stdout, "after"u8.ToArray());
        await streams.OnMessageAsync(liveExec);
        await streams.OnMessageAsync(new ProtocolMessage(MessageKind.Event, 0, 7,
            "shell.exit", ShellExecWire.EncodeExitCode(4)));

        await streams.BeginOpenAsync();
        await streams.CompleteOpenAsync(7, exec: false, closeMethod: "shell.close");
        await streams.EndOpenAsync();
        var liveInteractive = new ProtocolMessage(MessageKind.StreamData, 0, 7,
            "shell.output", "interactive"u8.ToArray());
        await streams.OnMessageAsync(liveInteractive);
        var secondLiveExec = new ProtocolMessage(MessageKind.StreamData, 0, 7,
            ShellExecWire.Stdout, "exec-again"u8.ToArray());
        await streams.OnMessageAsync(secondLiveExec);
        Assert(forwarded.Count == 4 && forwarded.TryDequeue(out var first) && first == earlyExec &&
            forwarded.TryDequeue(out var second) && second == liveExec &&
            forwarded.TryDequeue(out var third) && third == liveInteractive &&
            forwarded.TryDequeue(out var fourth) && fourth == secondLiveExec,
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
}
