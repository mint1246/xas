using System.Text.Json;
using Xas.Core;
using Xas.Core.FileSystem;
using Xas.Core.Security;

namespace Xas.Daemon.FileSystem;

/// <summary>Implements the peer-authorized filesystem RPC surface on top of local volumes.</summary>
public sealed class FileSystemService(PeerPermissionStore permissions)
{
    private readonly LocalFileSystemBackend _backend = new();

    public async ValueTask<ProtocolMessage> HandleAsync(string peerId, ProtocolMessage request,
        CancellationToken cancellationToken)
    {
        if (!permissions.IsAllowed(peerId, Capability.FileSystem))
            throw new UnauthorizedAccessException("Filesystem access is not granted on this device for this peer.");
        try
        {
            switch (request.Method)
            {
                case "fs.volumes":
                    RequireEmpty(request.Payload);
                    return Reply(request, RemoteFileSystemWire.Encode(_backend.GetVolumes()));
                case "fs.stat":
                {
                    var arg = Decode<RemotePath>(request.Payload);
                    return Reply(request, RemoteFileSystemWire.Encode(_backend.Stat(arg)));
                }
                case "fs.list":
                {
                    var arg = Decode<RemoteListPath>(request.Payload);
                    return Reply(request, RemoteFileSystemWire.Encode(_backend.List(arg)));
                }
                case "fs.read":
                {
                    var arg = Decode<RemoteReadRange>(request.Payload);
                    if (arg.Length < 0 || arg.Length > RemoteFileSystemWire.MaxChunkBytes) throw new InvalidDataException("Read length exceeds the 64 KiB limit.");
                    return Reply(request, _backend.Read(arg, cancellationToken));
                }
                case "fs.write":
                {
                    var arg = Decode<RemoteWriteRange>(request.Payload);
                    if (arg.Data.Length > RemoteFileSystemWire.MaxChunkBytes) throw new InvalidDataException("Write length exceeds the 64 KiB limit.");
                    var written = await _backend.WriteAsync(arg, cancellationToken).ConfigureAwait(false);
                    return Reply(request, RemoteFileSystemWire.Encode(new RemoteWriteResult(written)));
                }
                case "fs.create":
                    _backend.Create(Decode<RemoteCreatePath>(request.Payload));
                    return Reply(request, []);
                case "fs.delete":
                    _backend.Delete(Decode<RemoteDeletePath>(request.Payload));
                    return Reply(request, []);
                case "fs.rename":
                    _backend.Rename(Decode<RemoteRenamePath>(request.Payload));
                    return Reply(request, []);
                default: throw new NotSupportedException($"Unknown filesystem method: {request.Method}");
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid filesystem request payload.", ex); }
    }

    private static T Decode<T>(byte[] payload) => RemoteFileSystemWire.Decode<T>(payload);
    private static void RequireEmpty(byte[] payload) { if (payload.Length != 0) throw new InvalidDataException("This filesystem method takes no payload."); }
    private static ProtocolMessage Reply(ProtocolMessage request, byte[] payload) => new(MessageKind.Response, request.RequestId, request.StreamId, request.Method, payload);
}

/// <summary>Filesystem access is rooted at an explicitly selected local volume; paths cannot escape it.</summary>
internal sealed class LocalFileSystemBackend
{
    private const int PageSize = 512;
    private readonly Dictionary<string, string> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _volumeGate = new();

    public RemoteVolume[] GetVolumes()
    {
        lock (_volumeGate) _volumes.Clear();
        var result = new List<RemoteVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var root = Path.GetFullPath(drive.RootDirectory.FullName);
                var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16];
                lock (_volumeGate) _volumes[id] = root;
                var kind = drive.DriveType switch { DriveType.Removable => "removable", DriveType.Network => "network", DriveType.CDRom => "optical", _ => "fixed" };
                result.Add(new RemoteVolume(id, drive.VolumeLabel.Length == 0 ? root : drive.VolumeLabel, kind, IsReadOnly(root)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result.ToArray();
    }

    public RemoteFileStat Stat(RemotePath request)
    {
        var path = Resolve(request.VolumeId, request.Path, allowRoot: true);
        RejectLinks(path);
        var attrs = File.GetAttributes(path);
        var directory = attrs.HasFlag(FileAttributes.Directory);
        var info = directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        return new RemoteFileStat(info.Name.Length == 0 ? path : info.Name, directory, directory ? 0 : ((FileInfo)info).Length,
            new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(), attrs.HasFlag(FileAttributes.ReadOnly));
    }

    public RemoteDirectoryPage List(RemoteListPath request)
    {
        if (request.Offset < 0) throw new InvalidDataException("Directory offset cannot be negative.");
        var path = Resolve(request.VolumeId, request.Path, allowRoot: true);
        RejectLinks(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var entries = Directory.EnumerateFileSystemEntries(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Skip(request.Offset).Take(PageSize + 1).Select(entry =>
            {
                RejectLinks(entry);
                var attributes = File.GetAttributes(entry);
                var dir = attributes.HasFlag(FileAttributes.Directory);
                var info = dir ? (FileSystemInfo)new DirectoryInfo(entry) : new FileInfo(entry);
                return new RemoteFileEntry(info.Name, dir, dir ? 0 : ((FileInfo)info).Length,
                    new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
            }).ToArray();
        return new RemoteDirectoryPage(entries.Take(PageSize).ToArray(), entries.Length > PageSize);
    }

    public byte[] Read(RemoteReadRange request, CancellationToken cancellationToken)
    {
        if (request.Offset < 0) throw new InvalidDataException("File offset cannot be negative.");
        var path = Resolve(request.VolumeId, request.Path, allowRoot: false);
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = request.Offset;
        var buffer = new byte[request.Length];
        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        return count == buffer.Length ? buffer : buffer[..count];
    }

    public async ValueTask<long> WriteAsync(RemoteWriteRange request, CancellationToken cancellationToken)
    {
        if (request.Offset < 0) throw new InvalidDataException("File offset cannot be negative.");
        var path = Resolve(request.VolumeId, request.Path, allowRoot: false);
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        stream.Position = request.Offset;
        await stream.WriteAsync(request.Data, cancellationToken).ConfigureAwait(false);
        return request.Data.LongLength;
    }

    public void Create(RemoteCreatePath request)
    {
        var path = Resolve(request.VolumeId, request.Path, allowRoot: false);
        RejectLinks(Path.GetDirectoryName(path)!);
        RejectLinks(path);
        if (request.Directory)
        {
            if (Directory.Exists(path) && !request.Replace) throw new IOException("The directory already exists.");
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, request.Replace ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
    }

    public void Delete(RemoteDeletePath request)
    {
        var path = Resolve(request.VolumeId, request.Path, allowRoot: false);
        RejectLinks(path);
        if (request.Directory) Directory.Delete(path, recursive: false); else File.Delete(path);
    }

    public void Rename(RemoteRenamePath request)
    {
        var source = Resolve(request.VolumeId, request.Path, allowRoot: false);
        var destination = Resolve(request.VolumeId, request.NewPath, allowRoot: false);
        RejectLinks(source);
        RejectLinks(Path.GetDirectoryName(destination)!);
        RejectLinks(destination);
        if (Directory.Exists(source))
        {
            if (Directory.Exists(destination)) { if (!request.Replace) throw new IOException("Destination exists."); Directory.Delete(destination); }
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, request.Replace);
        }
    }

    private string Resolve(string id, string relative, bool allowRoot)
    {
        if (relative is null || relative.Length > RemoteFileSystemWire.MaxPathChars || relative.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid filesystem path.");
        string root;
        lock (_volumeGate)
            if (!_volumes.TryGetValue(id, out root!)) throw new IOException("Volume is no longer available. Refresh the volume list.");
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)) throw new InvalidDataException("Filesystem paths must be relative to their volume.");
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if ((!path.Equals(root, comparison) && !path.StartsWith(prefix, comparison)) || (!allowRoot && path.Equals(root, comparison)))
            throw new InvalidDataException("Filesystem path escapes its volume.");
        return path;
    }

    private static bool IsReadOnly(string path) => (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;

    private static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var current = full;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attrs = File.GetAttributes(current);
                if (attrs.HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException("Symbolic links and reparse points are not traversed by filesystem RPC.");
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
    }
}
