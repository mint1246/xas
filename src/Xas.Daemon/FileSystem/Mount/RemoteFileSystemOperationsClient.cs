using Xas.Core;
using Xas.Core.FileSystem;
using Xas.Core.Protocol;
using Xas.Daemon.Sessions;

namespace Xas.Daemon.FileSystem.Mount;

/// <summary>
/// Volume-scoped filesystem client used by native mount adapters. The transport delegate is kept
/// abstract so the filesystem semantics can be tested without a mounted WinFsp/FUSE filesystem.
/// </summary>
public sealed class RemoteFileSystemOperationsClient : IRemoteFileSystemOperations
{
    private readonly string _volumeId;
    private readonly Func<string, byte[], CancellationToken, ValueTask<ProtocolMessage>> _request;
    private readonly ushort _protocolVersion;

    public RemoteFileSystemOperationsClient(string volumeId,
        Func<string, byte[], CancellationToken, ValueTask<ProtocolMessage>> request, ushort protocolVersion = 1)
    {
        if (string.IsNullOrWhiteSpace(volumeId)) throw new ArgumentException("Volume ID is required.", nameof(volumeId));
        _volumeId = volumeId;
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _protocolVersion = protocolVersion;
    }

    public int MaxTransferBytes => _protocolVersion >= 2
        ? RemoteFileSystemWire.MaxChunkBytes
        : RemoteFileSystemWire.LegacyMaxChunkBytes;

    public static RemoteFileSystemOperationsClient ForPeer(PeerSession session, string volumeId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var version = session.Snapshot.Device?.Capabilities
            .FirstOrDefault(c => c.Capability == Capability.FileSystem)?.Version ?? 1;
        return new RemoteFileSystemOperationsClient(volumeId,
            (method, payload, token) => session.RequestAsync(PeerLane.Bulk, method, payload, token), version);
    }

    public static async ValueTask<RemoteVolume[]> GetVolumesAsync(PeerSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        ProtocolMessage reply;
        try { reply = await session.RequestAsync(PeerLane.Bulk, "fs.volumes", [], timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The peer did not respond to volume discovery within five seconds."); }
        return RemoteFileSystemWire.Decode<RemoteVolume[]>(reply.Payload);
    }

    public static async ValueTask EjectVolumeAsync(PeerSession session, string volumeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        var reply = await session.RequestAsync(PeerLane.Bulk, "fs.eject",
            RemoteFileSystemWire.Encode(new RemoteVolumeRequest(volumeId)), cancellationToken).ConfigureAwait(false);
        if (reply.Payload.Length != 0) throw new InvalidDataException("Invalid fs.eject response payload.");
    }

    public async ValueTask<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken) =>
        RemoteFileSystemWire.Decode<RemoteFileStat>((await RequestAsync("fs.stat",
            RemoteFileSystemWire.Encode(new RemotePath(_volumeId, Normalize(path))), cancellationToken).ConfigureAwait(false)).Payload);

    public async ValueTask<RemoteDirectoryPage> ListAsync(string path, int offset, CancellationToken cancellationToken) =>
        RemoteFileSystemWire.Decode<RemoteDirectoryPage>((await RequestAsync("fs.list",
            RemoteFileSystemWire.Encode(new RemoteListPath(_volumeId, Normalize(path), offset)), cancellationToken).ConfigureAwait(false)).Payload);

    public async ValueTask<byte[]> ReadAsync(string path, long offset, int length, CancellationToken cancellationToken)
    {
        if (length is < 0 || length > MaxTransferBytes)
            throw new ArgumentOutOfRangeException(nameof(length), $"Read length must be between 0 and {MaxTransferBytes} bytes.");
        return (await RequestAsync("fs.read",
            RemoteFileSystemWire.Encode(new RemoteReadRange(_volumeId, Normalize(path), offset, length)), cancellationToken).ConfigureAwait(false)).Payload;
    }

    public async ValueTask<int> WriteAsync(string path, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length > MaxTransferBytes)
            throw new ArgumentOutOfRangeException(nameof(data), $"Write length must not exceed {MaxTransferBytes} bytes.");
        var normalized = Normalize(path);
        var reply = _protocolVersion >= 2
            ? await RequestAsync("fs.write.v2",
                RemoteFileSystemWire.EncodeWriteV2(_volumeId, normalized, offset, data.Span), cancellationToken).ConfigureAwait(false)
            : await RequestAsync("fs.write",
                RemoteFileSystemWire.Encode(new RemoteWriteRange(_volumeId, normalized, offset, data.ToArray())), cancellationToken).ConfigureAwait(false);
        var result = RemoteFileSystemWire.Decode<RemoteWriteResult>(reply.Payload);
        if (result.BytesWritten is < 0 or > int.MaxValue) throw new InvalidDataException("Remote filesystem returned an invalid write count.");
        return checked((int)result.BytesWritten);
    }

    public async ValueTask CreateAsync(string path, bool directory, bool replace, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync("fs.create",
            RemoteFileSystemWire.Encode(new RemoteCreatePath(_volumeId, Normalize(path), directory, replace)), cancellationToken).ConfigureAwait(false);
        RequireEmpty(reply, "fs.create");
    }

    public async ValueTask DeleteAsync(string path, bool directory, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync("fs.delete",
            RemoteFileSystemWire.Encode(new RemoteDeletePath(_volumeId, Normalize(path), directory)), cancellationToken).ConfigureAwait(false);
        RequireEmpty(reply, "fs.delete");
    }

    public async ValueTask RenameAsync(string path, string newPath, bool replace, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync("fs.rename",
            RemoteFileSystemWire.Encode(new RemoteRenamePath(_volumeId, Normalize(path), Normalize(newPath), replace)), cancellationToken).ConfigureAwait(false);
        RequireEmpty(reply, "fs.rename");
    }

    public async ValueTask SetLengthAsync(string path, long length, CancellationToken cancellationToken)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        var reply = await RequestAsync("fs.truncate",
            RemoteFileSystemWire.Encode(new RemoteSetLength(_volumeId, Normalize(path), length)), cancellationToken).ConfigureAwait(false);
        RequireEmpty(reply, "fs.truncate");
    }

    public async ValueTask SetInfoAsync(string path, long? creationUnixMs, long? lastAccessUnixMs,
        long? lastWriteUnixMs, bool? readOnly, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync("fs.setinfo", RemoteFileSystemWire.Encode(new RemoteSetInfo(_volumeId,
            Normalize(path), creationUnixMs, lastAccessUnixMs, lastWriteUnixMs, readOnly)), cancellationToken).ConfigureAwait(false);
        RequireEmpty(reply, "fs.setinfo");
    }

    public async ValueTask FlushAsync(string path, bool directory, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync("fs.flush",
            RemoteFileSystemWire.Encode(new RemoteFlushPath(_volumeId, Normalize(path), directory)), cancellationToken).ConfigureAwait(false);
        RequireEmpty(reply, "fs.flush");
    }

    private async ValueTask<ProtocolMessage> RequestAsync(string method, byte[] payload, CancellationToken cancellationToken)
    {
        try { return await _request(method, payload, cancellationToken).ConfigureAwait(false); }
        catch (RemoteProtocolException ex)
        {
            var message = ex.RemoteMessage;
            if (method == "fs.flush" && ex.Code == "unknown" &&
                message.Contains("Unknown filesystem method", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("The remote peer does not support durable filesystem flushes.", ex);
            throw ex.Code switch
            {
                "file-not-found" => new FileNotFoundException(message),
                "directory-not-found" => new DirectoryNotFoundException(message),
                "access-denied" => new UnauthorizedAccessException(message),
                "io-error" => new IOException(message),
                "not-supported" => new NotSupportedException(message),
                "invalid-argument" => new InvalidDataException(message),
                _ => ex
            };
        }
    }

    private static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > RemoteFileSystemWire.MaxPathChars) throw new ArgumentOutOfRangeException(nameof(path));
        if (path.IndexOf('\0') >= 0) throw new ArgumentException("Filesystem path contains a NUL character.", nameof(path));
        // Native providers normally pass a rooted mount path. The wire protocol is volume-relative.
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static void RequireEmpty(ProtocolMessage reply, string method)
    {
        if (reply.Payload.Length != 0) throw new InvalidDataException($"{method} returned an unexpected response payload.");
    }
}
