using System.Threading.Channels;
using Xas.Core;
using Xas.Core.Protocol;

namespace Xas.Tests;

public static class ProtocolTests
{
    public static async Task RunAsync()
    {
        await FrameRoundTripAndPartialReadsAsync();
        await RejectMalformedFramesAsync();
        await MultiplexConcurrentRequestsAsync();
        await AllEventSubscribersAreAwaitedAsync();
        await TerminalFailureIsObservableAsync();
    }

    private static async Task FrameRoundTripAndPartialReadsAsync()
    {
        var stream = new FragmentingMemoryStream();
        await using (var sender = new BinaryFrameConnection(stream, leaveOpen: true))
            await sender.SendAsync(new ProtocolMessage(MessageKind.Request, 42, 9, "echo/世界", [0, 1, 255]), default);
        stream.Position = 0;
        await using var receiver = new BinaryFrameConnection(stream, leaveOpen: true);
        var got = await receiver.ReceiveAsync(default) ?? throw new Exception("Expected a frame.");
        Assert(got.Kind == MessageKind.Request && got.RequestId == 42 && got.StreamId == 9 &&
            got.Method == "echo/世界" && got.Payload.SequenceEqual(new byte[] { 0, 1, 255 }), "Frame fields did not round-trip.");
        Assert(await receiver.ReceiveAsync(default) is null, "Clean EOF should return null.");
    }

    private static async Task RejectMalformedFramesAsync()
    {
        var incomplete = new FragmentingMemoryStream([0, 0, 0, 13, 0, 1]);
        await using (var reader = new BinaryFrameConnection(incomplete, leaveOpen: true))
        {
            try { await reader.ReceiveAsync(default); throw new Exception("Truncated frame was accepted."); }
            catch (EndOfStreamException) { }
        }
        var invalidLength = new FragmentingMemoryStream([0xff, 0xff, 0xff, 0xff]);
        await using (var reader = new BinaryFrameConnection(invalidLength, leaveOpen: true))
        {
            try { await reader.ReceiveAsync(default); throw new Exception("Oversized frame was accepted."); }
            catch (InvalidDataException) { }
        }
    }

    private static async Task MultiplexConcurrentRequestsAsync()
    {
        var (left, right) = InMemoryFrameConnection.CreatePair();
        await using var client = new MultiplexedProtocolPeer(left, (_, _) => ValueTask.FromException<ProtocolMessage>(new Exception("Unexpected request.")));
        await using var server = new MultiplexedProtocolPeer(right, async (request, _) =>
        {
            var wait = request.Payload[0] == 1 ? 50 : 1;
            await Task.Delay(wait);
            return new ProtocolMessage(MessageKind.Response, 0, 0, request.Method, request.Payload);
        });
        var first = client.RequestAsync("echo", [1]).AsTask();
        var second = client.RequestAsync("echo", [2]).AsTask();
        var responses = await Task.WhenAll(first, second);
        Assert(responses[0].Payload[0] == 1 && responses[1].Payload[0] == 2, "Concurrent replies were mis-correlated.");
        Assert(responses[0].RequestId != responses[1].RequestId, "Concurrent requests reused an id.");
    }

    private static async Task AllEventSubscribersAreAwaitedAsync()
    {
        var (left, right) = InMemoryFrameConnection.CreatePair();
        await using var receiver = new MultiplexedProtocolPeer(left, (_, _) =>
            ValueTask.FromException<ProtocolMessage>(new Exception("Unexpected request.")));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawFirstCompletion = false;
        receiver.MessageReceived += async _ =>
        {
            await Task.Delay(20);
            first.TrySetResult();
        };
        receiver.MessageReceived += _ =>
        {
            sawFirstCompletion = first.Task.IsCompleted;
            second.TrySetResult();
            return ValueTask.CompletedTask;
        };
        await right.SendAsync(new ProtocolMessage(MessageKind.Event, 0, 0, "changed", []), default);
        await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(2));
        Assert(sawFirstCompletion, "The next subscriber ran before the prior async subscriber completed.");
    }

    private static async Task TerminalFailureIsObservableAsync()
    {
        var (left, right) = InMemoryFrameConnection.CreatePair();
        await using var receiver = new MultiplexedProtocolPeer(left, (_, _) =>
            ValueTask.FromException<ProtocolMessage>(new Exception("Unexpected request.")));
        await right.DisposeAsync();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(receiver.TerminalException is ChannelClosedException,
            "A disconnected peer must retain the receive failure for reconnect diagnostics.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FragmentingMemoryStream : MemoryStream
    {
        public FragmentingMemoryStream() { }
        public FragmentingMemoryStream(byte[] bytes) : base(bytes) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }

    private sealed class InMemoryFrameConnection : IFrameConnection
    {
        private readonly Channel<ProtocolMessage> _incoming;
        private readonly Channel<ProtocolMessage> _outgoing;
        private InMemoryFrameConnection(Channel<ProtocolMessage> incoming, Channel<ProtocolMessage> outgoing)
        { _incoming = incoming; _outgoing = outgoing; }
        public static (InMemoryFrameConnection Left, InMemoryFrameConnection Right) CreatePair()
        {
            var a = Channel.CreateUnbounded<ProtocolMessage>();
            var b = Channel.CreateUnbounded<ProtocolMessage>();
            return (new InMemoryFrameConnection(a, b), new InMemoryFrameConnection(b, a));
        }
        public ValueTask SendAsync(ProtocolMessage message, CancellationToken cancellationToken) =>
            _outgoing.Writer.WriteAsync(message, cancellationToken);
        public async ValueTask<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken) =>
            await _incoming.Reader.ReadAsync(cancellationToken);
        public ValueTask DisposeAsync()
        {
            _outgoing.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
