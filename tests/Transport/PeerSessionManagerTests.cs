using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Security;
using Xas.Daemon;
using Xas.Daemon.Input;
using Xas.Daemon.Sessions;

namespace Xas.Tests;

public static class PeerSessionManagerTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "xas-peer-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var identity = DeviceIdentity.LoadOrCreate(Path.Combine(root, "identity"), "local");
            var trust = new PeerTrustStore(Path.Combine(root, "trust"));
            var permissions = new PeerPermissionStore(Path.Combine(root, "trust"));
            var configuration = new LocalConfiguration(Path.Combine(root, "config"));
            await using var input = new InputControlService(permissions, new UnavailableInputBackend());
            var dispatcher = new RequestDispatcher(identity, permissions, configuration: configuration);
            var manager = new PeerSessionManager(identity, trust, permissions, input, dispatcher, configuration);
            var stream = new IdleAfterHandshakeStream();
            var runConnection = typeof(PeerSessionManager).GetMethod("RunConnectionAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var laneTask = (Task)runConnection.Invoke(manager,
                ["xas-test-peer", "test peer", stream, true, (PeerLane?)PeerLane.Realtime,
                    CancellationToken.None])!;
            await stream.Handshake.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var dialers = (ConcurrentDictionary<string, Task>)typeof(PeerSessionManager)
                .GetField("_dialers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
            dialers["xas-test-peer|Realtime"] = laneTask;

            // A connection that starts after its lifetime has already been cancelled
            // must close its protocol during registration instead of waiting forever.
            using var alreadyStopped = new CancellationTokenSource();
            alreadyStopped.Cancel();
            var lateStream = new IdleAfterHandshakeStream();
            var lateLaneTask = (Task)runConnection.Invoke(manager,
                ["xas-test-peer-late", "late test peer", lateStream, true, (PeerLane?)PeerLane.Realtime,
                    alreadyStopped.Token])!;
            await lateLaneTask.WaitAsync(TimeSpan.FromSeconds(2));

            await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await laneTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class IdleAfterHandshakeStream : Stream
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _position;

        public TaskCompletionSource Handshake { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _position == _current.Length)
            {
                if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                if (!_inbound.Reader.TryRead(out _current)) continue;
                _position = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _position);
            _current.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytes = buffer.ToArray();
            var methodLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(15, 2));
            var method = Encoding.UTF8.GetString(bytes, 17, methodLength);
            if (bytes[6] == (byte)Xas.Core.MessageKind.Request)
            {
                var response = bytes[..(17 + methodLength)];
                BinaryPrimitives.WriteUInt32BigEndian(response, (uint)(response.Length - 4));
                response[6] = (byte)Xas.Core.MessageKind.Response;
                _inbound.Writer.TryWrite(response);
                if (method == "session.lane.open") Handshake.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
