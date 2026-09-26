using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Fsp;
using Xas.Core.FileSystem;
using WinFspFileInfo = Fsp.Interop.FileInfo;
using WinFspVolumeInfo = Fsp.Interop.VolumeInfo;

namespace Xas.Daemon.FileSystem.Mount;

/// <summary>Exposes one remote XAS volume through the official WinFsp .NET provider.</summary>
[SupportedOSPlatform("windows")]
public sealed class WinFspRemoteFileSystemMountAdapter : IRemoteFileSystemMountAdapter
{
    private FileSystemHost? _host;
    private CancellationTokenSource? _mountLifetime;

    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try { _ = FileSystemHost.Version(); return true; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
            { return false; }
        }
    }

    public string PlatformName => "WinFsp";
    public string? MountedAt => _host?.MountPoint();

    public ValueTask MountAsync(string mountPoint, RemoteVolume volume, IRemoteFileSystemOperations remoteFileSystem,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return ValueTask.FromException(new PlatformNotSupportedException("WinFsp mounts require Windows."));
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(remoteFileSystem);
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is not null) return ValueTask.FromException(new InvalidOperationException("A remote filesystem is already mounted."));
        if (!IsAvailable) return ValueTask.FromException(new PlatformNotSupportedException("The WinFsp runtime/driver is not installed."));

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var filesystem = new RemoteWinFspFileSystem(volume, remoteFileSystem, lifetime.Token);
        var host = new FileSystemHost(filesystem);
        try
        {
            var target = string.IsNullOrWhiteSpace(mountPoint) ? null : NormalizeMountPoint(mountPoint);
            var status = host.Mount(target, null!, false, 0);
            if (status < 0) throw new IOException($"WinFsp could not mount the remote volume (NTSTATUS 0x{status:X8}).");
            _mountLifetime = lifetime;
            _host = host;
            return ValueTask.CompletedTask;
        }
        catch
        {
            lifetime.Cancel();
            lifetime.Dispose();
            host.Dispose();
            throw;
        }
    }

    public ValueTask UnmountAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var host = Interlocked.Exchange(ref _host, null);
        var lifetime = Interlocked.Exchange(ref _mountLifetime, null);
        if (host is null)
        {
            lifetime?.Dispose();
            return ValueTask.CompletedTask;
        }

        lifetime?.Cancel();
        try { host.Unmount(); }
        finally
        {
            host.Dispose();
            lifetime?.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await UnmountAsync(CancellationToken.None).ConfigureAwait(false);

    private static string NormalizeMountPoint(string mountPoint)
    {
        var value = mountPoint.Trim();
        if (value.Length == 1 && char.IsAsciiLetter(value[0])) value += ":";
        if (value.Length == 2 && char.IsAsciiLetter(value[0]) && value[1] == ':') return value.ToUpperInvariant();
        if (Path.IsPathFullyQualified(value)) return Path.GetFullPath(value);
        throw new ArgumentException("WinFsp mount point must be a drive letter such as E: or an absolute directory.", nameof(mountPoint));
    }
}

/// <summary>
/// WinFsp callback surface for an XAS remote volume. Handles deliberately contain only a remote path;
/// persistent remote file handles and metadata/read-ahead caches can be added later without changing
/// the native mount contract.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteWinFspFileSystem : FileSystemBase
{
    private const int AllocationUnit = 4096;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly byte[] DefaultSecurity = CreateDefaultSecurity();
    private readonly RemoteVolume _volume;
    private readonly IRemoteFileSystemOperations _remote;
    private readonly CancellationToken _mountLifetime;

    public RemoteWinFspFileSystem(RemoteVolume volume, IRemoteFileSystemOperations remote, CancellationToken mountLifetime = default)
    {
        _volume = volume ?? throw new ArgumentNullException(nameof(volume));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _mountLifetime = mountLifetime;
    }

    public override int Init(object hostObject)
    {
        var host = (FileSystemHost)hostObject;
        host.SectorSize = AllocationUnit;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 750;
        host.VolumeInfoTimeout = 1000;
        host.DirInfoTimeout = 750;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.ReparsePoints = false;
        host.NamedStreams = false;
        host.ExtendedAttributes = false;
        host.PostCleanupWhenModifiedOnly = true;
        host.PostDispositionWhenNecessaryOnly = true;
        host.PassQueryDirectoryPattern = true;
        host.FlushAndPurgeOnCleanup = false;
        host.FileSystemName = "XAS";
        host.VolumeSerialNumber = StableVolumeSerial(_volume.Id);
        return STATUS_SUCCESS;
    }

    public override int ExceptionHandler(Exception exception) => MapException(exception);

    public override int GetVolumeInfo(out WinFspVolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = ClampToUInt64(_volume.TotalBytes);
        volumeInfo.FreeSize = ClampToUInt64(_volume.FreeBytes);
        volumeInfo.SetVolumeLabel(SanitizeVolumeLabel(_volume.Name));
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        try
        {
            var stat = Await(ct => _remote.StatAsync(NormalizePath(fileName), ct));
            fileAttributes = Attributes(stat);
            if (securityDescriptor is not null) securityDescriptor = DefaultSecurity;
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc,
        out WinFspFileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        try
        {
            var path = NormalizePath(fileName);
            var directory = (createOptions & FILE_DIRECTORY_FILE) != 0;
            Await(ct => _remote.CreateAsync(path, directory, replace: false, ct));
            if ((fileAttributes & (uint)FileAttributes.ReadOnly) != 0)
                Await(ct => _remote.SetInfoAsync(path, null, null, null, true, ct));
            var stat = Await(ct => _remote.StatAsync(path, ct));
            var handle = new RemoteHandle(path, stat.Directory);
            fileDesc = handle;
            fileInfo = ToFileInfo(stat);
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out WinFspFileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        try
        {
            var path = NormalizePath(fileName);
            var stat = Await(ct => _remote.StatAsync(path, ct));
            if ((createOptions & FILE_DIRECTORY_FILE) != 0 && !stat.Directory) return STATUS_NOT_A_DIRECTORY;
            if ((createOptions & FILE_NON_DIRECTORY_FILE) != 0 && stat.Directory) return STATUS_FILE_IS_A_DIRECTORY;
            fileDesc = new RemoteHandle(path, stat.Directory);
            fileInfo = ToFileInfo(stat);
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes,
        ulong allocationSize, out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            var handle = RequireHandle(fileDesc);
            if (handle.Directory) return STATUS_FILE_IS_A_DIRECTORY;
            Await(ct => _remote.SetLengthAsync(handle.Path, 0, ct));
            if (replaceFileAttributes || fileAttributes != 0)
                Await(ct => _remote.SetInfoAsync(handle.Path, null, null, null,
                    (fileAttributes & (uint)FileAttributes.ReadOnly) != 0, ct));
            fileInfo = ToFileInfo(Await(ct => _remote.StatAsync(handle.Path, ct)));
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var handle = fileDesc as RemoteHandle;
        if (handle is null || (!handle.DeletePending && (flags & CleanupDelete) == 0)) return;
        try
        {
            lock (handle.Gate)
            {
                if (handle.Deleted) return;
                Await(ct => _remote.DeleteAsync(handle.Path, handle.Directory, ct));
                handle.Deleted = true;
            }
        }
        catch { /* Cleanup cannot report a status; SetDelete/CanDelete already validated the request. */ }
    }

    public override void Close(object fileNode, object fileDesc) { }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;
        try
        {
            var handle = RequireHandle(fileDesc);
            if (handle.Directory) return STATUS_FILE_IS_A_DIRECTORY;
            var stat = Await(ct => _remote.StatAsync(handle.Path, ct));
            if (offset >= (ulong)Math.Max(0, stat.Length)) return STATUS_END_OF_FILE;

            var remaining = checked((int)Math.Min(length, int.MaxValue));
            var destinationOffset = 0;
            while (remaining > 0)
            {
                var request = Math.Min(remaining, RemoteFileSystemWire.MaxChunkBytes);
                var data = Await(ct => _remote.ReadAsync(handle.Path, checked((long)offset + destinationOffset), request, ct));
                if (data.Length == 0) break;
                Marshal.Copy(data, 0, IntPtr.Add(buffer, destinationOffset), data.Length);
                destinationOffset += data.Length;
                remaining -= data.Length;
                if (data.Length < request) break;
            }
            bytesTransferred = checked((uint)destinationOffset);
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out WinFspFileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        try
        {
            if (_volume.ReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;
            var handle = RequireHandle(fileDesc);
            if (handle.Directory) return STATUS_FILE_IS_A_DIRECTORY;
            var stat = Await(ct => _remote.StatAsync(handle.Path, ct));
            var effectiveOffset = writeToEndOfFile ? Math.Max(0, stat.Length) : checked((long)offset);
            var requested = checked((int)Math.Min(length, int.MaxValue));
            if (constrainedIo)
            {
                if (effectiveOffset >= stat.Length)
                {
                    fileInfo = ToFileInfo(stat);
                    return STATUS_SUCCESS;
                }
                requested = checked((int)Math.Min(requested, stat.Length - effectiveOffset));
            }

            var transferred = 0;
            while (transferred < requested)
            {
                var count = Math.Min(requested - transferred, RemoteFileSystemWire.MaxChunkBytes);
                var data = new byte[count];
                Marshal.Copy(IntPtr.Add(buffer, transferred), data, 0, count);
                var written = Await(ct => _remote.WriteAsync(handle.Path, effectiveOffset + transferred, data, ct));
                if (written <= 0) break;
                transferred += written;
                if (written < count) break;
            }
            bytesTransferred = checked((uint)transferred);
            fileInfo = ToFileInfo(Await(ct => _remote.StatAsync(handle.Path, ct)));
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int Flush(object fileNode, object fileDesc, out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            if (fileDesc is not RemoteHandle handle) return STATUS_SUCCESS;
            fileInfo = ToFileInfo(Await(ct => _remote.StatAsync(handle.Path, ct)));
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            var handle = RequireHandle(fileDesc);
            fileInfo = ToFileInfo(Await(ct => _remote.StatAsync(handle.Path, ct)));
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime,
        ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            if (_volume.ReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;
            var handle = RequireHandle(fileDesc);
            Await(ct => _remote.SetInfoAsync(handle.Path,
                FileTimeToUnixMs(creationTime), FileTimeToUnixMs(lastAccessTime), FileTimeToUnixMs(lastWriteTime),
                fileAttributes == 0 ? null : (fileAttributes & (uint)FileAttributes.ReadOnly) != 0, ct));
            fileInfo = ToFileInfo(Await(ct => _remote.StatAsync(handle.Path, ct)));
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize,
        out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            if (_volume.ReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;
            if (newSize > long.MaxValue) return STATUS_INVALID_PARAMETER;
            var handle = RequireHandle(fileDesc);
            if (handle.Directory) return STATUS_FILE_IS_A_DIRECTORY;
            var stat = Await(ct => _remote.StatAsync(handle.Path, ct));
            if (!setAllocationSize || (ulong)Math.Max(0, stat.Length) > newSize)
                Await(ct => _remote.SetLengthAsync(handle.Path, checked((long)newSize), ct));
            fileInfo = ToFileInfo(Await(ct => _remote.StatAsync(handle.Path, ct)));
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        try
        {
            if (_volume.ReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;
            var handle = RequireHandle(fileDesc);
            if (handle.Directory)
            {
                var page = Await(ct => _remote.ListAsync(handle.Path, 0, ct));
                if (page.Entries.Length != 0 || page.HasMore) return STATUS_DIRECTORY_NOT_EMPTY;
            }
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int SetDelete(object fileNode, object fileDesc, string fileName, bool deleteFile)
    {
        try
        {
            var handle = RequireHandle(fileDesc);
            if (deleteFile)
            {
                var status = CanDelete(fileNode, fileDesc, fileName);
                if (status != STATUS_SUCCESS) return status;
            }
            handle.DeletePending = deleteFile;
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        try
        {
            if (_volume.ReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;
            var handle = RequireHandle(fileDesc);
            var newPath = NormalizePath(newFileName);
            Await(ct => _remote.RenameAsync(handle.Path, newPath, replaceIfExists, ct));
            lock (handle.Gate)
            {
                handle.Path = newPath;
                handle.DirectoryEntries = null;
            }
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return MapException(ex); }
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out WinFspFileInfo fileInfo)
    {
        fileName = null!;
        fileInfo = default;
        try
        {
            var handle = RequireHandle(fileDesc);
            if (!handle.Directory) return false;
            var entries = GetDirectoryEntries(handle);
            var cursor = context as DirectoryCursor;
            if (cursor is null)
            {
                var start = 0;
                if (!string.IsNullOrEmpty(marker))
                {
                    while (start < entries.Length && string.Compare(entries[start].Name, marker, StringComparison.OrdinalIgnoreCase) <= 0)
                        start++;
                }
                cursor = new DirectoryCursor(start, NormalizePattern(pattern));
                context = cursor;
            }

            while (cursor.Index < entries.Length)
            {
                var entry = entries[cursor.Index++];
                if (cursor.Pattern is not null && !FileSystemName.MatchesSimpleExpression(cursor.Pattern, entry.Name, ignoreCase: true))
                    continue;
                fileName = entry.Name;
                fileInfo = ToFileInfo(entry);
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private RemoteFileEntry[] GetDirectoryEntries(RemoteHandle handle)
    {
        lock (handle.Gate)
        {
            if (handle.DirectoryEntries is not null) return handle.DirectoryEntries;
            var entries = new List<RemoteFileEntry>();
            var offset = 0;
            while (true)
            {
                var page = Await(ct => _remote.ListAsync(handle.Path, offset, ct));
                entries.AddRange(page.Entries);
                offset += page.Entries.Length;
                if (!page.HasMore) break;
                if (page.Entries.Length == 0) throw new InvalidDataException("Remote directory pagination made no progress.");
            }
            handle.DirectoryEntries = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return handle.DirectoryEntries;
        }
    }

    private static RemoteHandle RequireHandle(object fileDesc) => fileDesc as RemoteHandle
        ?? throw new InvalidDataException("WinFsp callback received an invalid XAS file handle.");

    private T Await<T>(Func<CancellationToken, ValueTask<T>> action)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_mountLifetime);
        timeout.CancelAfter(OperationTimeout);
        try { return action(timeout.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!_mountLifetime.IsCancellationRequested)
        { throw new TimeoutException("Remote filesystem operation timed out."); }
    }

    private void Await(Func<CancellationToken, ValueTask> action)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_mountLifetime);
        timeout.CancelAfter(OperationTimeout);
        try { action(timeout.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!_mountLifetime.IsCancellationRequested)
        { throw new TimeoutException("Remote filesystem operation timed out."); }
    }

    private static string NormalizePath(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var path = fileName.Replace('\\', '/').TrimStart('/');
        if (path == ".") return string.Empty;
        return path;
    }

    private static string? NormalizePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return null;
        return pattern.Replace('<', '*').Replace('>', '?').Replace('"', '.');
    }

    private static WinFspFileInfo ToFileInfo(RemoteFileStat stat)
    {
        var length = stat.Directory ? 0UL : checked((ulong)Math.Max(0, stat.Length));
        var time = UnixMsToFileTime(stat.LastWriteUnixMs);
        return new WinFspFileInfo
        {
            FileAttributes = Attributes(stat),
            AllocationSize = RoundAllocation(length),
            FileSize = length,
            CreationTime = time,
            LastAccessTime = time,
            LastWriteTime = time,
            ChangeTime = time,
            HardLinks = 1
        };
    }

    private static WinFspFileInfo ToFileInfo(RemoteFileEntry entry)
    {
        var length = entry.Directory ? 0UL : checked((ulong)Math.Max(0, entry.Length));
        var time = UnixMsToFileTime(entry.LastWriteUnixMs);
        return new WinFspFileInfo
        {
            FileAttributes = entry.Directory ? (uint)FileAttributes.Directory : (uint)FileAttributes.Normal,
            AllocationSize = RoundAllocation(length),
            FileSize = length,
            CreationTime = time,
            LastAccessTime = time,
            LastWriteTime = time,
            ChangeTime = time,
            HardLinks = 1
        };
    }

    private static uint Attributes(RemoteFileStat stat)
    {
        var attrs = stat.Directory ? FileAttributes.Directory : FileAttributes.Normal;
        if (stat.ReadOnly) attrs |= FileAttributes.ReadOnly;
        return (uint)attrs;
    }

    private int MapException(Exception exception)
    {
        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            exception = aggregate.InnerException!;
        return exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => STATUS_OBJECT_NAME_NOT_FOUND,
            UnauthorizedAccessException => _volume.ReadOnly ? STATUS_MEDIA_WRITE_PROTECTED : STATUS_ACCESS_DENIED,
            TimeoutException => STATUS_IO_TIMEOUT,
            OperationCanceledException => STATUS_IO_TIMEOUT,
            ArgumentException or InvalidDataException => STATUS_INVALID_PARAMETER,
            NotSupportedException or PlatformNotSupportedException => STATUS_NOT_SUPPORTED,
            IOException io when io.Message.Contains("not empty", StringComparison.OrdinalIgnoreCase) => STATUS_DIRECTORY_NOT_EMPTY,
            IOException io when io.Message.Contains("exists", StringComparison.OrdinalIgnoreCase) => STATUS_OBJECT_NAME_COLLISION,
            IOException => STATUS_UNEXPECTED_IO_ERROR,
            _ => STATUS_UNEXPECTED_IO_ERROR
        };
    }

    private static ulong RoundAllocation(ulong length) => length == 0 ? 0 : ((length + AllocationUnit - 1) / AllocationUnit) * AllocationUnit;

    private static ulong UnixMsToFileTime(long unixMs)
    {
        try { return checked((ulong)DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToFileTimeUtc()); }
        catch (ArgumentOutOfRangeException) { return 0; }
    }

    private static long? FileTimeToUnixMs(ulong fileTime)
    {
        if (fileTime == 0 || fileTime > long.MaxValue) return null;
        try { return new DateTimeOffset(DateTime.FromFileTimeUtc((long)fileTime)).ToUnixTimeMilliseconds(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static ulong ClampToUInt64(long? value) => value is > 0 ? checked((ulong)value.Value) : 0;

    private static string SanitizeVolumeLabel(string value)
    {
        var label = string.IsNullOrWhiteSpace(value) ? "XAS Remote" : value.Trim();
        return label.Length <= 32 ? label : label[..32];
    }

    private static uint StableVolumeSerial(string id)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in id) hash = (hash ^ ch) * 16777619;
            return hash;
        }
    }

    private static byte[] CreateDefaultSecurity()
    {
        var descriptor = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private sealed class RemoteHandle(string path, bool directory)
    {
        public object Gate { get; } = new();
        public string Path { get; set; } = path;
        public bool Directory { get; } = directory;
        public bool DeletePending { get; set; }
        public bool Deleted { get; set; }
        public RemoteFileEntry[]? DirectoryEntries { get; set; }
    }

    private sealed class DirectoryCursor(int index, string? pattern)
    {
        public int Index { get; set; } = index;
        public string? Pattern { get; } = pattern;
    }
}
