using System.Runtime.InteropServices;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Services;
using Xas.Daemon.Shell;

namespace Xas.Daemon;

public sealed class RequestDispatcher(DeviceIdentity identity, PeerPermissionStore permissions)
{
    private readonly ProcessShellBackend _shell = new();

    public async ValueTask<ProtocolMessage> HandleAsync(string peerId, ProtocolMessage request,
        Func<ProtocolMessage, CancellationToken, ValueTask> send, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "device.info":
            {
                var info = new DeviceInfo(identity.DeviceId, Environment.MachineName,
                    RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
                    [new CapabilityVersion(Capability.Shell, 2)]);
                return Reply(request, JsonSerializer.SerializeToUtf8Bytes(info));
            }
            case "device.ping":
                return Reply(request, Array.Empty<byte>());
            case "shell.run":
            {
                if (!permissions.IsAllowed(peerId, Capability.Shell))
                    throw new UnauthorizedAccessException("Shell access is not granted on this device for this peer.");
                var shellRequest = ShellWire.DecodeRequest(request.Payload);
                if (shellRequest.Elevated) throw new NotSupportedException("Privileged shell is not implemented.");
                if (shellRequest.Mode == ShellMode.Interactive) throw new NotSupportedException("Interactive PTY/ConPTY shell is not implemented.");
                await using var stdout = new BoundedCaptureStream(450_000);
                await using var stderr = new BoundedCaptureStream(450_000);
                var exitCode = await _shell.RunAsync(shellRequest, Stream.Null, stdout, stderr, cancellationToken);
                return Reply(request, ShellWire.EncodeResult(new ShellResult(exitCode, stdout.ToArray(), stderr.ToArray(),
                    stdout.Truncated || stderr.Truncated)));
            }
            case "shell.stream":
            {
                if (!permissions.IsAllowed(peerId, Capability.Shell))
                    throw new UnauthorizedAccessException("Shell access is not granted on this device for this peer.");
                var (shellRequest, inputBytes) = ShellWire.DecodeInvocation(request.Payload);
                if (shellRequest.Elevated) throw new NotSupportedException("Privileged shell is not implemented.");
                if (shellRequest.Mode == ShellMode.Interactive) throw new NotSupportedException("Interactive PTY/ConPTY shell is not implemented.");
                using var stdin = new MemoryStream(inputBytes, writable: false);
                await using var stdout = new ProtocolOutputStream(request, 1, send, cancellationToken);
                await using var stderr = new ProtocolOutputStream(request, 2, send, cancellationToken);
                var exitCode = await _shell.RunAsync(shellRequest, stdin, stdout, stderr, cancellationToken);
                await stdout.EndAsync();
                await stderr.EndAsync();
                return Reply(request, ShellWire.EncodeResult(new ShellResult(exitCode, [], [], false)));
            }
            default:
                throw new NotSupportedException($"Unknown request method: {request.Method}");
        }
    }

    private static ProtocolMessage Reply(ProtocolMessage request, byte[] payload) =>
        new(MessageKind.Response, request.RequestId, request.StreamId, request.Method, payload);
}

internal sealed class ProtocolOutputStream(ProtocolMessage request, uint streamId,
    Func<ProtocolMessage, CancellationToken, ValueTask> send, CancellationToken cancellationToken) : Stream
{
    private const int ChunkBytes = 64 * 1024;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        while (!buffer.IsEmpty)
        {
            var length = Math.Min(buffer.Length, ChunkBytes);
            await send(new ProtocolMessage(MessageKind.StreamData, request.RequestId, streamId, request.Method,
                buffer[..length].ToArray()), cancellationToken).ConfigureAwait(false);
            buffer = buffer[length..];
        }
    }
    public ValueTask EndAsync() => send(new ProtocolMessage(MessageKind.StreamEnd, request.RequestId,
        streamId, request.Method, []), cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class BoundedCaptureStream(int maxBytes) : Stream
{
    private readonly MemoryStream _buffer = new();
    public bool Truncated { get; private set; }
    public byte[] ToArray() => _buffer.ToArray();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _buffer.Length;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var take = Math.Min(buffer.Length, maxBytes - (int)_buffer.Length);
        if (take > 0) _buffer.Write(buffer[..take]);
        if (take < buffer.Length) Truncated = true;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
}
