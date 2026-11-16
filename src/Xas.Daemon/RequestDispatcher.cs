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
        clipboardBackend ?? CreateClipboardBackend());
    private readonly LinuxDisplayMetadataService _displayMetadata = new();
    private readonly FileSystemService _fileSystem = new(
        permissions,
        configuration ?? new LocalConfiguration());

    internal ClipboardService Clipboard => _clipboard;
    internal FileSystemService FileSystem => _fileSystem;

    public async ValueTask<ProtocolMessage> HandleAsync(string peerId, ProtocolMessage request,
        Func<ProtocolMessage, CancellationToken, ValueTask> send, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "device.info":
                return CreateDeviceInfoReply(request, GetCapabilities());
            case "device.ping":
                return Reply(request, Array.Empty<byte>());
            case "device.path.probe":
                return EchoPathProbe(request);
            case "display.info":
                return await GetDisplayInfoAsync(peerId, request, cancellationToken)
                    .ConfigureAwait(false);
            case "clipboard.get":
            case "clipboard.set":
                return await _clipboard.HandleAsync(peerId, request, cancellationToken)
                    .ConfigureAwait(false);
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
            case "fs.flush":
            case "fs.eject":
                return await _fileSystem.HandleAsync(peerId, request, cancellationToken)
                    .ConfigureAwait(false);
            case "shell.run":
                return await RunShellAsync(peerId, request, cancellationToken)
                    .ConfigureAwait(false);
            case "shell.stream":
                return await RunShellStreamAsync(
                    peerId,
                    request,
                    send,
                    cancellationToken).ConfigureAwait(false);
            default:
                throw new NotSupportedException($"Unknown request method: {request.Method}");
        }
    }

    private static ITextClipboardBackend CreateClipboardBackend() => OperatingSystem.IsWindows()
        ? new WindowsTextClipboard()
        : new LinuxTextClipboard();

    private List<CapabilityVersion> GetCapabilities()
    {
        var capabilities = new List<CapabilityVersion>
        {
            new(Capability.Shell, (ushort)(_interactive.IsAvailable ? 3 : 2)),
            new(Capability.FileSystem, 2)
        };
        if (_clipboard.IsAvailable)
        {
            capabilities.Add(new(Capability.Clipboard, 1));
        }
        if (inputVersion?.Invoke() is > 0 and var version)
        {
            capabilities.Add(new(Capability.Input, version));
        }
        if (_displayMetadata.IsAvailable)
        {
            capabilities.Add(new(Capability.Display, 1));
        }
        return capabilities;
    }

    private static ProtocolMessage EchoPathProbe(ProtocolMessage request)
    {
        if (request.Payload.Length > 768 * 1024)
        {
            throw new InvalidDataException("Path probe payload exceeds 768 KiB.");
        }

        // Echoing exercises TLS and protocol framing in both directions for path selection.
        return Reply(request, request.Payload);
    }

    private async ValueTask<ProtocolMessage> GetDisplayInfoAsync(
        string peerId,
        ProtocolMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Payload.Length != 0)
        {
            throw new InvalidDataException("display.info takes no payload.");
        }
        if (!permissions.IsAllowed(peerId, Capability.Input))
        {
            throw new UnauthorizedAccessException(
                "Display metadata requires this peer's Input grant.");
        }
        if (!_displayMetadata.IsAvailable)
        {
            throw new PlatformNotSupportedException("Linux display metadata is unavailable.");
        }

        var metadata = await _displayMetadata.GetPrimaryDisplayAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PlatformNotSupportedException(
                "No active Linux display metadata was returned.");
        return Reply(request, JsonSerializer.SerializeToUtf8Bytes(metadata));
    }

    private ProtocolMessage CreateDeviceInfoReply(
        ProtocolMessage request,
        IReadOnlyList<CapabilityVersion> capabilities)
    {
        var info = new DeviceInfo(
            identity.DeviceId,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            capabilities);
        return Reply(request, JsonSerializer.SerializeToUtf8Bytes(info));
    }

    private async ValueTask<ProtocolMessage> RunShellAsync(
        string peerId,
        ProtocolMessage request,
        CancellationToken cancellationToken)
    {
        EnsureShellPermission(peerId);
        var shellRequest = ShellWire.DecodeRequest(request.Payload);
        EnsureShellModeIsSupported(peerId, shellRequest);

        await using var stdout = new BoundedCaptureStream(450_000);
        await using var stderr = new BoundedCaptureStream(450_000);
        var backend = ResolveShellBackend(shellRequest);
        var exitCode = await backend.RunAsync(
            shellRequest,
            Stream.Null,
            stdout,
            stderr,
            cancellationToken);
        return CreateShellResultReply(
            request,
            exitCode,
            stdout.ToArray(),
            stderr.ToArray(),
            stdout.Truncated || stderr.Truncated);
    }

    private async ValueTask<ProtocolMessage> RunShellStreamAsync(
        string peerId,
        ProtocolMessage request,
        Func<ProtocolMessage, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken)
    {
        EnsureShellPermission(peerId);
        var (shellRequest, inputBytes) = ShellWire.DecodeInvocation(request.Payload);
        EnsureShellModeIsSupported(peerId, shellRequest);

        using var stdin = new MemoryStream(inputBytes, writable: false);
        await using var stdout = new ProtocolOutputStream(request, 1, send, cancellationToken);
        await using var stderr = new ProtocolOutputStream(request, 2, send, cancellationToken);
        var backend = ResolveShellBackend(shellRequest);
        var exitCode = await backend.RunAsync(
            shellRequest,
            stdin,
            stdout,
            stderr,
            cancellationToken);
        await stdout.EndAsync();
        await stderr.EndAsync();
        return CreateShellResultReply(request, exitCode, [], [], false);
    }

    private static ProtocolMessage CreateShellResultReply(
        ProtocolMessage request,
        int exitCode,
        byte[] stdout,
        byte[] stderr,
        bool truncated)
    {
        var result = new ShellResult(exitCode, stdout, stderr, truncated);
        return Reply(request, ShellWire.EncodeResult(result));
    }

    private void EnsureShellPermission(string peerId)
    {
        if (!permissions.IsAllowed(peerId, Capability.Shell))
        {
            throw new UnauthorizedAccessException(
                "Shell access is not granted on this device for this peer.");
        }
    }

    private void EnsureShellModeIsSupported(string peerId, ShellRequest shellRequest)
    {
        if (shellRequest.Elevated
            && OperatingSystem.IsWindows()
            && !permissions.IsAllowed(peerId, Capability.PrivilegedShell))
        {
            throw new UnauthorizedAccessException(
                "Privileged shell access is not granted on this device for this peer.");
        }

        if (shellRequest.Mode == ShellMode.Interactive)
        {
            throw new NotSupportedException("Interactive PTY/ConPTY shell is not implemented.");
        }
    }

    private IShellBackend ResolveShellBackend(ShellRequest shellRequest)
    {
        if (shellRequest.Elevated && OperatingSystem.IsWindows())
        {
            return new WindowsAdminBrokerClient();
        }

        return _shell;
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
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        while (!buffer.IsEmpty)
        {
            var length = Math.Min(buffer.Length, ChunkBytes);
            var message = new ProtocolMessage(
                MessageKind.StreamData,
                request.RequestId,
                streamId,
                request.Method,
                buffer[..length].ToArray());
            await send(message, cancellationToken).ConfigureAwait(false);
            buffer = buffer[length..];
        }
    }
    public ValueTask EndAsync()
    {
        var message = new ProtocolMessage(
            MessageKind.StreamEnd,
            request.RequestId,
            streamId,
            request.Method,
            []);
        return send(message, cancellationToken);
    }
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

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
    public override void Write(byte[] buffer, int offset, int count)
    {
        Write(buffer.AsSpan(offset, count));
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var take = Math.Min(buffer.Length, maxBytes - (int)_buffer.Length);
        if (take > 0) _buffer.Write(buffer[..take]);
        if (take < buffer.Length) Truncated = true;
    }
    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer.Dispose();
        }

        base.Dispose(disposing);
    }
}
