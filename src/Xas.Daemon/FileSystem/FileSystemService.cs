using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.FileSystem;
using Xas.Core.Security;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Xas.Daemon.FileSystem;

/// <summary>Implements the peer-authorized filesystem RPC surface on top of local volumes.</summary>
public sealed class FileSystemService(PeerPermissionStore permissions, LocalConfiguration configuration)
{
    private readonly LocalFileSystemBackend _backend = new(configuration);

    internal RemoteVolume[] GetVolumesSnapshot() => _backend.GetVolumes();

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
                    if (arg.Length < 0 || arg.Length > RemoteFileSystemWire.MaxChunkBytes) throw new InvalidDataException("Read length exceeds the filesystem transfer limit.");
                    return Reply(request, _backend.Read(arg, cancellationToken));
                }
                case "fs.write":
                {
                    var arg = Decode<RemoteWriteRange>(request.Payload);
                    if (arg.Data.Length > RemoteFileSystemWire.LegacyMaxChunkBytes) throw new InvalidDataException("Legacy filesystem write length exceeds 64 KiB.");
                    var written = await _backend.WriteAsync(arg, cancellationToken).ConfigureAwait(false);
                    return Reply(request, RemoteFileSystemWire.Encode(new RemoteWriteResult(written)));
                }
                case "fs.write.v2":
                {
                    var arg = RemoteFileSystemWire.DecodeWriteV2(request.Payload);
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
                case "fs.truncate":
                    _backend.SetLength(Decode<RemoteSetLength>(request.Payload));
                    return Reply(request, []);
                case "fs.setinfo":
                    _backend.SetInfo(Decode<RemoteSetInfo>(request.Payload));
                    return Reply(request, []);
                case "fs.flush":
                {
                    var arg = Decode<RemoteFlushPath>(request.Payload);
                    _backend.Flush(arg);
                    return Reply(request, []);
                }
                case "fs.eject":
                    await _backend.EjectAsync(Decode<RemoteVolumeRequest>(request.Payload), cancellationToken).ConfigureAwait(false);
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
internal sealed class LocalFileSystemBackend(LocalConfiguration configuration)
{
    private const int PageSize = 512;
    private readonly Dictionary<string, VolumeRoot> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _volumeGate = new();

    public RemoteVolume[] GetVolumes()
    {
        var roots = new Dictionary<string, VolumeRoot>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RemoteVolume>();

        foreach (var export in configuration.FileSystemExports)
        {
            try
            {
                var root = Path.GetFullPath(export.Path);
                if (!Directory.Exists(root)) continue;
                RejectLinks(root);
                var id = "export-" + export.Id;
                roots[id] = new VolumeRoot(root, export.ReadOnly, "export", null);
                var storage = GetStorageInfo(root);
                result.Add(new RemoteVolume(id, export.Name, "export", export.ReadOnly,
                    storage.TotalBytes, storage.FreeBytes, storage.FileSystem));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (configuration.AutoExposeRemovable)
        {
            foreach (var volume in EnumerateRemovableVolumes())
            {
                try
                {
                    var root = Path.GetFullPath(volume.Root);
                    if (!Directory.Exists(root)) continue;
                    RejectLinks(root);
                    var id = "removable-" + StableVolumeId(root);
                    if (roots.ContainsKey(id)) continue;
                    roots[id] = new VolumeRoot(root, volume.ReadOnly, "removable", volume.SourceDevice);
                    var storage = GetStorageInfo(root);
                    result.Add(new RemoteVolume(id, volume.Name, "removable", volume.ReadOnly,
                        storage.TotalBytes, storage.FreeBytes, storage.FileSystem));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        if (configuration.AutoExposeMainDrive)
        {
            foreach (var volume in EnumerateMainVolume())
            {
                try
                {
                    var root = Path.GetFullPath(volume.Root);
                    if (!Directory.Exists(root)) continue;
                    RejectLinks(root);
                    var id = "main-" + StableVolumeId(root);
                    roots[id] = new VolumeRoot(root, volume.ReadOnly, "fixed", null);
                    var storage = GetStorageInfo(root);
                    result.Add(new RemoteVolume(id, volume.Name, "fixed", volume.ReadOnly,
                        storage.TotalBytes, storage.FreeBytes, storage.FileSystem));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        lock (_volumeGate)
        {
            _volumes.Clear();
            foreach (var (id, root) in roots) _volumes[id] = root;
        }
        return result.ToArray();
    }

    public RemoteFileStat Stat(RemotePath request)
    {
        var (path, volume) = Resolve(request.VolumeId, request.Path, allowRoot: true);
        RejectLinks(path);
        var attrs = File.GetAttributes(path);
        var directory = attrs.HasFlag(FileAttributes.Directory);
        var info = directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        return new RemoteFileStat(info.Name.Length == 0 ? path : info.Name, directory, directory ? 0 : ((FileInfo)info).Length,
            new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(), volume.ReadOnly || attrs.HasFlag(FileAttributes.ReadOnly));
    }

    public RemoteDirectoryPage List(RemoteListPath request)
    {
        if (request.Offset < 0) throw new InvalidDataException("Directory offset cannot be negative.");
        var (path, _) = Resolve(request.VolumeId, request.Path, allowRoot: true);
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
        var (path, _) = Resolve(request.VolumeId, request.Path, allowRoot: false);
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
        => await WriteAsync(request.VolumeId, request.Path, request.Offset, request.Data, cancellationToken).ConfigureAwait(false);

    public async ValueTask<long> WriteAsync(RemoteBinaryWriteRange request, CancellationToken cancellationToken)
        => await WriteAsync(request.VolumeId, request.Path, request.Offset, request.Data, cancellationToken).ConfigureAwait(false);

    private async ValueTask<long> WriteAsync(string volumeId, string remotePath, long offset,
        ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (offset < 0) throw new InvalidDataException("File offset cannot be negative.");
        var (path, volume) = Resolve(volumeId, remotePath, allowRoot: false);
        EnsureWritable(volume);
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        stream.Position = offset;
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        return data.Length;
    }

    public void Create(RemoteCreatePath request)
    {
        var (path, volume) = Resolve(request.VolumeId, request.Path, allowRoot: false);
        EnsureWritable(volume);
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
        var (path, volume) = Resolve(request.VolumeId, request.Path, allowRoot: false);
        EnsureWritable(volume);
        RejectLinks(path);
        if (request.Directory) Directory.Delete(path, recursive: false); else File.Delete(path);
    }

    public void Rename(RemoteRenamePath request)
    {
        var (source, volume) = Resolve(request.VolumeId, request.Path, allowRoot: false);
        EnsureWritable(volume);
        var (destination, destinationVolume) = Resolve(request.VolumeId, request.NewPath, allowRoot: false);
        EnsureWritable(destinationVolume);
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

    public void SetLength(RemoteSetLength request)
    {
        if (request.Length < 0) throw new InvalidDataException("File length cannot be negative.");
        var (path, volume) = Resolve(request.VolumeId, request.Path, allowRoot: false);
        EnsureWritable(volume);
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(request.Length);
    }

    public void Flush(RemoteFlushPath request)
    {
        var (path, _) = Resolve(request.VolumeId, request.Path, allowRoot: request.Directory);
        RejectLinks(path);
        if (request.Directory)
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            FlushDirectoryToDisk(path);
        }
        else
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Flush(flushToDisk: true);
        }
    }

    private static void FlushDirectoryToDisk(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            const int O_RDONLY = 0;
            const int O_DIRECTORY = 0x10000;
            const int O_CLOEXEC = 0x80000;
            var fd = NativeDirectoryFlush.open(path, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
            if (fd < 0) throw new IOException("Could not open directory for durable flush.", Marshal.GetLastPInvokeError());
            try
            {
                if (NativeDirectoryFlush.fsync(fd) != 0)
                    throw new IOException("Could not durably flush directory metadata.", Marshal.GetLastPInvokeError());
            }
            finally { _ = NativeDirectoryFlush.close(fd); }
            return;
        }
        throw new PlatformNotSupportedException("Directory durable flush is not supported on this platform.");
    }

    private static class NativeDirectoryFlush
    {
        [DllImport("libc", SetLastError = true, ExactSpelling = true)] internal static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true, ExactSpelling = true)] internal static extern int fsync(int fd);
        [DllImport("libc", SetLastError = true, ExactSpelling = true)] internal static extern int close(int fd);
    }

    public void SetInfo(RemoteSetInfo request)
    {
        var (path, volume) = Resolve(request.VolumeId, request.Path, allowRoot: false);
        EnsureWritable(volume);
        RejectLinks(path);
        if (request.CreationUnixMs is { } creation && OperatingSystem.IsWindows())
            File.SetCreationTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(creation).UtcDateTime);
        if (request.LastAccessUnixMs is { } access)
            File.SetLastAccessTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(access).UtcDateTime);
        if (request.LastWriteUnixMs is { } write)
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(write).UtcDateTime);
        if (request.ReadOnly is { } readOnly)
        {
            var attributes = File.GetAttributes(path);
            attributes = readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
            File.SetAttributes(path, attributes);
        }
    }

    public async ValueTask EjectAsync(RemoteVolumeRequest request, CancellationToken cancellationToken)
    {
        var volume = GetVolume(request.VolumeId);
        if (!string.Equals(volume.Kind, "removable", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only removable volumes can be ejected.");
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Remote safe eject is currently implemented for Linux removable volumes.");
        if (string.IsNullOrWhiteSpace(volume.SourceDevice) || !volume.SourceDevice.StartsWith("/dev/", StringComparison.Ordinal))
            throw new PlatformNotSupportedException("The removable volume has no UDisks block-device source.");

        var udisksctl = FindOnPath("udisksctl")
            ?? throw new PlatformNotSupportedException("udisksctl is unavailable; install UDisks2 for remote safe eject.");
        var info = new ProcessStartInfo(udisksctl)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("unmount");
        info.ArgumentList.Add("--block-device");
        info.ArgumentList.Add(volume.SourceDevice);
        info.ArgumentList.Add("--no-user-interaction");
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Could not start udisksctl for remote eject.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Timed out while asking UDisks to unmount the removable volume.");
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new IOException($"UDisks could not safely unmount the removable volume: {detail.Trim()}");
        }
        lock (_volumeGate) _volumes.Remove(request.VolumeId);
    }

    private (string Path, VolumeRoot Volume) Resolve(string id, string relative, bool allowRoot)
    {
        if (relative is null || relative.Length > RemoteFileSystemWire.MaxPathChars || relative.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid filesystem path.");
        var volume = GetVolume(id);
        var root = volume.Root;
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)) throw new InvalidDataException("Filesystem paths must be relative to their volume.");
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if ((!path.Equals(root, comparison) && !path.StartsWith(prefix, comparison)) || (!allowRoot && path.Equals(root, comparison)))
            throw new InvalidDataException("Filesystem path escapes its volume.");
        return (path, volume);
    }

    private VolumeRoot GetVolume(string id)
    {
        lock (_volumeGate)
            if (_volumes.TryGetValue(id, out var cached)) return cached;
        _ = GetVolumes();
        lock (_volumeGate)
            if (_volumes.TryGetValue(id, out var refreshed)) return refreshed;
        throw new IOException("Volume is no longer available or is not exported.");
    }

    private static void EnsureWritable(VolumeRoot volume)
    {
        if (volume.ReadOnly) throw new UnauthorizedAccessException("This filesystem export is read-only.");
    }

    private static IEnumerable<MountedVolume> EnumerateRemovableVolumes()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Removable) continue;
                var root = drive.RootDirectory.FullName;
                var name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? root : drive.VolumeLabel;
                yield return new MountedVolume(root, name, false, null);
            }
            yield break;
        }

        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/mountinfo")) yield break;
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10) continue;
            var separator = Array.IndexOf(fields, "-");
            if (separator < 6 || separator + 2 >= fields.Length) continue;
            var device = fields[2];
            var mountPoint = DecodeMountInfoPath(fields[4]);
            if (mountPoint == "/" || !Path.IsPathRooted(mountPoint) || !IsLinuxRemovableDevice(device)) continue;
            var readOnly = fields[5].Split(',').Contains("ro", StringComparer.Ordinal);
            var name = Path.GetFileName(mountPoint.TrimEnd('/'));
            if (string.IsNullOrWhiteSpace(name)) name = mountPoint;
            var sourceDevice = DecodeMountInfoPath(fields[separator + 2]);
            if (!sourceDevice.StartsWith("/dev/", StringComparison.Ordinal)) sourceDevice = null;
            yield return new MountedVolume(mountPoint, name, readOnly, sourceDevice);
        }
    }

    private static IEnumerable<MountedVolume> EnumerateMainVolume()
    {
        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            if (!string.IsNullOrEmpty(root)) yield return new MountedVolume(root, root, false, null);
            yield break;
        }

        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/mountinfo")) yield break;
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var separator = Array.IndexOf(fields, "-");
            if (separator < 6 || separator + 3 >= fields.Length || fields[4] != "/") continue;
            var readOnly = fields[5].Split(',').Contains("ro", StringComparer.Ordinal) ||
                           fields[separator + 3].Split(',').Contains("ro", StringComparer.Ordinal);
            yield return new MountedVolume("/", "root", readOnly, null);
            yield break;
        }
    }

    private static bool IsLinuxRemovableDevice(string majorMinor)
    {
        if (majorMinor.Length == 0 || majorMinor.Any(c => !char.IsAsciiDigit(c) && c != ':')) return false;
        var link = Path.Combine("/sys/dev/block", majorMinor);
        if (!File.Exists(link) && !Directory.Exists(link)) return false;
        try
        {
            FileSystemInfo source = new FileInfo(link);
            var target = source.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null) return false;
            var full = target.FullName.Replace('\\', '/');
            if (full.Contains("/usb", StringComparison.OrdinalIgnoreCase)) return true;
            var current = target.FullName;
            while (!string.IsNullOrWhiteSpace(current) && current.StartsWith("/sys/", StringComparison.Ordinal))
            {
                var removable = Path.Combine(current, "removable");
                if (File.Exists(removable) && string.Equals(File.ReadAllText(removable).Trim(), "1", StringComparison.Ordinal))
                    return true;
                var parent = Path.GetDirectoryName(current);
                if (parent is null || parent == current) break;
                current = parent;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static string DecodeMountInfoPath(string value) => value
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);

    private static string StableVolumeId(string root) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root)))[..16];

    private static StorageInfo GetStorageInfo(string root)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var full = Path.GetFullPath(root);
            var drive = DriveInfo.GetDrives()
                .Where(item => item.IsReady && full.StartsWith(Path.GetFullPath(item.RootDirectory.FullName), comparison))
                .OrderByDescending(item => item.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive is null) return default;
            return new StorageInfo(drive.TotalSize, drive.AvailableFreeSpace,
                string.IsNullOrWhiteSpace(drive.DriveFormat) ? null : drive.DriveFormat);
        }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return null;
    }

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

    private sealed record VolumeRoot(string Root, bool ReadOnly, string Kind, string? SourceDevice);
    private sealed record MountedVolume(string Root, string Name, bool ReadOnly, string? SourceDevice);
    private readonly record struct StorageInfo(long? TotalBytes, long? FreeBytes, string? FileSystem);
}
