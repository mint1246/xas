using System.Runtime.InteropServices;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Services;
using Xas.Core.Privileged;
using Xas.Core.Configuration;
using Xas.Daemon.Shell;
using Xas.Daemon.Clipboard;
using Xas.Daemon.Display;
using Xas.Daemon.FileSystem;

namespace Xas.Daemon;

public sealed class RequestDispatcher(DeviceIdentity identity, PeerPermissionStore permissions,
    Func<ushort>? inputVersion = null, ITextClipboardBackend? clipboardBackend = null,
    LocalConfiguration? configuration = null)
{
    private readonly ProcessShellBackend _shell = new();
    private readonly IInteractiveShellBackend _interactive = OperatingSystem.IsWindows()
        ? new WindowsConPtyBackend() : new LinuxPtyBackend();
    private readonly ClipboardService _clipboard = new(identity.DeviceId, permissions,
        clipboardBackend ?? (OperatingSystem.IsWindows() ? new WindowsTextClipboard() : new LinuxTextClipboard()));
    private readonly LinuxDisplayMetadataService _display = new();
    private readonly FileSystemService _fileSystem = new(permissions, configuration ?? new LocalConfiguration());

    internal ClipboardService Clipboard => _clipboard;
    internal FileSystemService FileSystem => _fileSystem;

    public async ValueTask<ProtocolMessage> HandleAsync(string peerId, ProtocolMessage request,
        Func<ProtocolMessage, CancellationToken, ValueTask> send, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "device.info":
            {
                var capabilities = new List<CapabilityVersion>
                {
                    new(Capability.Shell, (ushort)(_interactive.IsAvailable ? 3 : 2)),
                    new(Capability.FileSystem, 2)
                };
                if (_clipboard.IsAvailable) capabilities.Add(new(Capability.Clipboard, 1));
                if (inputVersion?.Invoke() is > 0 and var version) capabilities.Add(new(Capability.Input, version));
                if (_display.IsAvailable) capabilities.Add(new(Capability.Display, 1));
                var info = new DeviceInfo(identity.DeviceId, Environment.MachineName,
                    RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), capabilities);
                return Reply(request, JsonSerializer.SerializeToUtf8Bytes(info));
            }
            case "device.ping":
                return Reply(request, Array.Empty<byte>());
            case "device.path.probe":
                if (request.Payload.Length > 768 * 1024)
                    throw new InvalidDataException("Path probe payload exceeds 768 KiB.");
                // Echo the caller's bytes without changing them. This intentionally exercises the same TLS
                // and protocol framing in both directions so peers can choose a real transport path instead
                // of trusting interface metrics or Wi-Fi PHY rates.
                return Reply(request, request.Payload);
            case "display.info":
            {
                if (request.Payload.Length != 0) throw new InvalidDataException("display.info takes no payload.");
                if (!permissions.IsAllowed(peerId, Capability.Input))
                    throw new UnauthorizedAccessException("Display metadata requires this peer's Input grant.");
                if (!_display.IsAvailable) throw new PlatformNotSupportedException("Linux display metadata is unavailable.");
                var metadata = await _display.GetPrimaryDisplayAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new PlatformNotSupportedException("No active Linux display metadata was returned.");
                return Reply(request, JsonSerializer.SerializeToUtf8Bytes(metadata));
            }
            case "clipboard.get":
            case "clipboard.set":
                return await _clipboard.HandleAsync(peerId, request, cancellationToken);
            case "fs.volumes":
            case "fs.stat":
            case "fs.list":
            case "fs.read":
            case "fs.write":
            case "fs.write.v2":
            case "fs.create":
            case "fs.delete":
            case "fs.rename":
            case "fs.truncate":
            case "fs.setinfo":
            case "fs.eject":
                return await _fileSystem.HandleAsync(peerId, request, cancellationToken).ConfigureAwait(false);
            case "shell.run":
            {
                if (!permissions.IsAllowed(peerId, Capability.Shell))
                    throw new UnauthorizedAccessException("Shell access is not granted on this device for this peer.");
                var shellRequest = ShellWire.DecodeRequest(request.Payload);
                if (shellRequest.Elevated && OperatingSystem.IsWindows() && !permissions.IsAllowed(peerId, Capability.PrivilegedShell))
                    throw new UnauthorizedAccessException("Privileged shell access is not granted on this device for this peer.");
                if (shellRequest.Mode == ShellMode.Interactive) throw new NotSupportedException("Interactive PTY/ConPTY shell is not implemented.");
                await using var stdout = new BoundedCaptureStream(450_000);
                await using var stderr = new BoundedCaptureStream(450_000);
                IShellBackend backend = shellRequest.Elevated && OperatingSystem.IsWindows()
                    ? new WindowsAdminBrokerClient() : _shell;
                var exitCode = await backend.RunAsync(shellRequest, Stream.Null, stdout, stderr, cancellationToken);
                return Reply(request, ShellWire.EncodeResult(new ShellResult(exitCode, stdout.ToArray(), stderr.ToArray(),
                    stdout.Truncated || stderr.Truncated)));
            }
            case "shell.stream":
            {
                if (!permissions.IsAllowed(peerId, Capability.Shell))
                    throw new UnauthorizedAccessException("Shell access is not granted on this device for this peer.");
                var (shellRequest, inputBytes) = ShellWire.DecodeInvocation(request.Payload);
                if (shellRequest.Elevated && OperatingSystem.IsWindows() && !permissions.IsAllowed(peerId, Capability.PrivilegedShell))
                    throw new UnauthorizedAccessException("Privileged shell access is not granted on this device for this peer.");
                if (shellRequest.Mode == ShellMode.Interactive) throw new NotSupportedException("Interactive PTY/ConPTY shell is not implemented.");
                using var stdin = new MemoryStream(inputBytes, writable: false);
                await using var stdout = new ProtocolOutputStream(request, 1, send, cancellationToken);
                await using var stderr = new ProtocolOutputStream(request, 2, send, cancellationToken);
                IShellBackend backend = shellRequest.Elevated && OperatingSystem.IsWindows()
                    ? new WindowsAdminBrokerClient() : _shell;
                var exitCode = await backend.RunAsync(shellRequest, stdin, stdout, stderr, cancellationToken);
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
