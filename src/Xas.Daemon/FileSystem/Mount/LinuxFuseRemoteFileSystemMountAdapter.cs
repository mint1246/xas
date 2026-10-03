using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FuseDotNet;
using FuseDotNet.Extensions;
using LTRData.Extensions.Native.Memory;
using Xas.Core.FileSystem;

namespace Xas.Daemon.FileSystem.Mount;

/// <summary>
/// OS-neutral, synchronously callable core used by the Linux FUSE callback layer. Keeping the remote
/// filesystem logic outside FuseDotNet lets Windows CI exercise paging, chunking, mutation, and timeout
/// behavior without loading Linux-native FUSE structures.
/// </summary>
public sealed class RemoteFuseFileSystemCore
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private readonly IRemoteFileSystemOperations _remote;
    private readonly CancellationToken _mountLifetime;

    public RemoteFuseFileSystemCore(RemoteVolume volume, IRemoteFileSystemOperations remote,
        CancellationToken mountLifetime = default)
    {
        Volume = volume ?? throw new ArgumentNullException(nameof(volume));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _mountLifetime = mountLifetime;
    }

    public RemoteVolume Volume { get; }

    public RemoteFileStat Stat(string path) => Await(ct => _remote.StatAsync(Normalize(path), ct));

    public RemoteFileEntry[] List(string path)
    {
        var normalized = Normalize(path);
        var entries = new List<RemoteFileEntry>();
        var offset = 0;
        while (true)
        {
            var page = Await(ct => _remote.ListAsync(normalized, offset, ct));
            entries.AddRange(page.Entries);
            offset += page.Entries.Length;
            if (!page.HasMore) break;
            if (page.Entries.Length == 0)
                throw new InvalidDataException("Remote directory pagination made no progress.");
        }
        return entries.ToArray();
    }

    public RemoteFuseDirectoryEntry[] ReadDirectory(string path, long offset)
        => ReadDirectory(List(path), offset);

    public static RemoteFuseDirectoryEntry[] ReadDirectory(RemoteFileEntry[] remote, long offset)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (offset < 0 || offset > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(offset));
        var all = new RemoteFuseDirectoryEntry[remote.Length + 2];
        all[0] = new RemoteFuseDirectoryEntry(".", 1,
            new RemoteFileEntry(".", true, 0, 0));
        all[1] = new RemoteFuseDirectoryEntry("..", 2,
            new RemoteFileEntry("..", true, 0, 0));
        for (var i = 0; i < remote.Length; i++)
            all[i + 2] = new RemoteFuseDirectoryEntry(remote[i].Name, i + 3, remote[i]);
        return all.Skip((int)Math.Min(offset, all.Length)).ToArray();
    }

    public byte[] Read(string path, long position, int length)
    {
        if (position < 0 || length < 0) throw new ArgumentOutOfRangeException();
        var normalized = Normalize(path);
        using var output = new MemoryStream(length);
        var remaining = length;
        var offset = position;
        while (remaining > 0)
        {
            var request = Math.Min(remaining, _remote.MaxTransferBytes);
            var chunk = Await(ct => _remote.ReadAsync(normalized, offset, request, ct));
            if (chunk.Length == 0) break;
            output.Write(chunk);
            remaining -= chunk.Length;
            offset = checked(offset + chunk.Length);
            if (chunk.Length < request) break;
        }
        return output.ToArray();
    }

    public int Write(string path, long position, ReadOnlySpan<byte> data)
    {
        EnsureWritable();
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        var normalized = Normalize(path);
        var written = 0;
        while (written < data.Length)
        {
            var count = Math.Min(data.Length - written, _remote.MaxTransferBytes);
            var chunk = data.Slice(written, count).ToArray();
            var actual = Await(ct => _remote.WriteAsync(normalized, checked(position + written), chunk, ct));
            if (actual <= 0) break;
            if (actual > count) throw new InvalidDataException("Remote filesystem wrote more bytes than requested.");
            written += actual;
            if (actual < count) break;
        }
        return written;
    }

    public void Create(string path, bool directory, bool replace = false)
    {
        EnsureWritable();
        Await(ct => _remote.CreateAsync(Normalize(path), directory, replace, ct));
    }

    public void Delete(string path, bool directory)
    {
        EnsureWritable();
        Await(ct => _remote.DeleteAsync(Normalize(path), directory, ct));
    }

    public void Rename(string path, string newPath, bool replace)
    {
        EnsureWritable();
        Await(ct => _remote.RenameAsync(Normalize(path), Normalize(newPath), replace, ct));
    }

    public void SetLength(string path, long length)
    {
        EnsureWritable();
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Await(ct => _remote.SetLengthAsync(Normalize(path), length, ct));
    }

    public void SetTimes(string path, long? lastAccessUnixMs, long? lastWriteUnixMs)
    {
        EnsureWritable();
        Await(ct => _remote.SetInfoAsync(Normalize(path), null, lastAccessUnixMs, lastWriteUnixMs, null, ct));
    }

    public void Flush(string path, bool directory = false) =>
        Await(ct => _remote.FlushAsync(Normalize(path), directory, ct));

    private void EnsureWritable()
    {
        if (Volume.ReadOnly) throw new UnauthorizedAccessException("This remote volume is read-only.");
    }

    private T Await<T>(Func<CancellationToken, ValueTask<T>> operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_mountLifetime);
        timeout.CancelAfter(OperationTimeout);
        try { return operation(timeout.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!_mountLifetime.IsCancellationRequested)
        { throw new TimeoutException("Remote filesystem operation timed out."); }
    }

    private void Await(Func<CancellationToken, ValueTask> operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_mountLifetime);
        timeout.CancelAfter(OperationTimeout);
        try { operation(timeout.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!_mountLifetime.IsCancellationRequested)
        { throw new TimeoutException("Remote filesystem operation timed out."); }
    }

    internal static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IndexOf('\0') >= 0 || path.Length > RemoteFileSystemWire.MaxPathChars)
            throw new ArgumentException("Invalid remote filesystem path.", nameof(path));
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized == "." ? string.Empty : normalized;
    }
}

public sealed record RemoteFuseDirectoryEntry(string Name, long NextOffset, RemoteFileEntry Entry);

/// <summary>Linux FUSE3 native mount adapter backed by the remote fs.* RPC surface.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxFuseRemoteFileSystemMountAdapter : IRemoteFileSystemMountAdapter
{
    private static int _resolverInstalled;
    private readonly object _gate = new();
    private CancellationTokenSource? _mountLifetime;
    private Task? _mountTask;
    private string? _mountPoint;
    private string? _connectionId;
    private bool _createdMountPoint;

    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/dev/fuse") || FindOnPath("fusermount3") is null) return false;
            EnsureFuseResolver();
            return CanLoadFuse3();
        }
    }

    public string PlatformName => "FUSE3";
    public string? MountedAt { get { lock (_gate) return _mountPoint; } }

    public async ValueTask MountAsync(string mountPoint, RemoteVolume volume,
        IRemoteFileSystemOperations remoteFileSystem, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("FUSE mounts require Linux.");
        EnsureFuseResolver();
        if (!IsAvailable)
            throw new PlatformNotSupportedException("FUSE3 is unavailable. XAS requires libfuse3, fusermount3, and access to /dev/fuse.");
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPoint);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(remoteFileSystem);
        cancellationToken.ThrowIfCancellationRequested();

        var fullMountPoint = Path.GetFullPath(mountPoint);
        lock (_gate)
            if (_mountTask is not null) throw new InvalidOperationException("A remote filesystem is already mounted by this adapter.");

        // A previous daemon may have exited before detaching its mount. Inspect mountinfo
        // before touching the directory, since a disconnected FUSE mount cannot be read.
        var abandonedConnection = FindConnectionId(fullMountPoint);
        if (abandonedConnection is not null)
        {
            await RunFuseUnmountAsync(fullMountPoint, cancellationToken).ConfigureAwait(false);
            AbortConnection(abandonedConnection);
        }

        var existed = Directory.Exists(fullMountPoint);
        Directory.CreateDirectory(fullMountPoint);
        if (Directory.EnumerateFileSystemEntries(fullMountPoint).Any())
            throw new IOException($"FUSE mount point is not empty: {fullMountPoint}");

        // A completed or disconnected UI/CLI request must not cancel an existing mount.
        var lifetime = new CancellationTokenSource();
        var operations = new RemoteFuseOperations(new RemoteFuseFileSystemCore(volume, remoteFileSystem, lifetime.Token));
        var args = new[]
        {
            "xas-fuse", "-f", "-s", "-o",
            $"fsname=xas,subtype=xas,attr_timeout=0.75,entry_timeout=0.75,negative_timeout=0.25",
            fullMountPoint
        };
        var mountTask = Task.Factory.StartNew(() => operations.Mount(args), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

        lock (_gate)
        {
            _mountLifetime = lifetime;
            _mountTask = mountTask;
            _mountPoint = fullMountPoint;
            _createdMountPoint = !existed;
        }

        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(10));
            while (!IsMounted(fullMountPoint))
            {
                if (mountTask.IsCompleted)
                {
                    await mountTask.ConfigureAwait(false);
                    throw new IOException("FUSE mount loop ended before the mount became visible.");
                }
                await Task.Delay(50, startup.Token).ConfigureAwait(false);
            }
            lock (_gate) _connectionId = FindConnectionId(fullMountPoint);
        }
        catch
        {
            try { await UnmountAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async ValueTask UnmountAsync(CancellationToken cancellationToken)
    {
        Task? mountTask;
        CancellationTokenSource? lifetime;
        string? mountPoint;
        string? connectionId;
        bool removeDirectory;
        lock (_gate)
        {
            mountTask = _mountTask;
            lifetime = _mountLifetime;
            mountPoint = _mountPoint;
            connectionId = _connectionId;
            removeDirectory = _createdMountPoint;
        }
        if (mountTask is null || mountPoint is null)
        {
            lifetime?.Dispose();
            return;
        }

        // Keep ownership if the OS rejects unmounting, so the manager can retry it.
        if (IsMounted(mountPoint)) await RunFuseUnmountAsync(mountPoint, cancellationToken).ConfigureAwait(false);
        lifetime?.Cancel();
        // Lazy detach alone leaves a connection alive while callers hold references.
        // Abort only this adapter's FUSE connection so pending kernel calls can finish.
        if (connectionId is not null) AbortConnection(connectionId);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await mountTask.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("FUSE mount loop did not exit after unmount."); }
        }
        finally
        {
            lock (_gate)
            {
                _mountTask = null;
                _mountLifetime = null;
                _mountPoint = null;
                _connectionId = null;
                _createdMountPoint = false;
            }
            lifetime?.Dispose();
            if (removeDirectory)
            {
                try { if (Directory.Exists(mountPoint) && !Directory.EnumerateFileSystemEntries(mountPoint).Any()) Directory.Delete(mountPoint); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public async ValueTask DisposeAsync() => await UnmountAsync(CancellationToken.None).ConfigureAwait(false);

    private static void AbortConnection(string connectionId)
    {
        try { File.WriteAllText($"/sys/fs/fuse/connections/{connectionId}/abort", "1"); }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"Could not abort detached XAS FUSE connection {connectionId}: {ex.Message}"); }
    }

    private static bool CanLoadFuse3()
    {
        nint handle;
        if (NativeLibrary.TryLoad("libfuse3.so.3", out handle) || NativeLibrary.TryLoad("libfuse3.so", out handle) ||
            NativeLibrary.TryLoad("fuse3", out handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }
        return false;
    }

    private static void EnsureFuseResolver()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Fuse).Assembly, static (libraryName, _, _) =>
            {
                if (!string.Equals(libraryName, "fuse3", StringComparison.Ordinal)) return nint.Zero;
                foreach (var candidate in new[] { "libfuse3.so.3", "libfuse3.so", "fuse3" })
                    if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
                return nint.Zero;
            });
        }
        catch (InvalidOperationException)
        {
            // A host can install one resolver per assembly. If another resolver already exists, leave it
            // in place; the explicit availability probe below will still fail cleanly if FUSE cannot load.
        }
    }

    private static async Task RunFuseUnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        var tool = FindOnPath("fusermount3")
            ?? throw new PlatformNotSupportedException("fusermount3 is unavailable.");
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Detach even when an application still has the mount as its current directory.
        info.ArgumentList.Add("-uz");
        info.ArgumentList.Add(mountPoint);
        using var process = Process.Start(info) ?? throw new IOException("Could not start fusermount3.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("fusermount3 timed out while unmounting the remote filesystem.");
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException($"fusermount3 could not unmount {mountPoint}: {(string.IsNullOrWhiteSpace(error) ? output : error).Trim()}");
    }

    private static bool IsMounted(string mountPoint)
    {
        if (!File.Exists("/proc/self/mountinfo")) return false;
        var expected = Path.GetFullPath(mountPoint).TrimEnd('/');
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5) continue;
            var current = DecodeMountInfoPath(fields[4]).TrimEnd('/');
            if (string.Equals(current, expected, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string? FindConnectionId(string mountPoint)
    {
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var separator = Array.IndexOf(fields, "-");
            if (separator < 6 || separator + 1 >= fields.Length || fields[separator + 1] != "fuse.xas" ||
                DecodeMountInfoPath(fields[4]) != mountPoint) continue;
            var device = fields[2].Split(':');
            if (device.Length == 2 && device[0] == "0" && device[1].Length > 0 && device[1].All(char.IsAsciiDigit))
                return device[1];
        }
        return null;
    }

    private static string DecodeMountInfoPath(string value) => value
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);

    internal static string? FindOnPath(string executable)
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
}

[SupportedOSPlatform("linux")]
internal sealed class RemoteFuseOperations(RemoteFuseFileSystemCore core) : IFuseOperations
{
    private static readonly PosixResult TimedOut = 110; // Linux ETIMEDOUT
    private static readonly PosixResult DirectoryNotEmpty = 39; // Linux ENOTEMPTY
    private static readonly uint UserId = NativeUser.geteuid();
    private static readonly uint GroupId = NativeUser.getegid();

    public PosixResult GetAttr(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseFileStat stat, ref FuseFileInfo fileInfo)
    {
        try { stat = ToFuseStat(core.Stat(PathOf(fileNamePtr))); return PosixResult.Success; }
        catch (Exception ex) { stat = default; return Map(ex); }
    }

    public PosixResult OpenDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        try
        {
            var path = PathOf(fileNamePtr);
            if (!core.Stat(path).Directory) return PosixResult.ENOTDIR;
            fileInfo.Context = core.List(path);
            return PosixResult.Success;
        }
        catch (Exception ex) { return Map(ex); }
    }

    public PosixResult ReadDir(ReadOnlyNativeMemory<byte> fileNamePtr, out IEnumerable<FuseDirEntry> entries,
        ref FuseFileInfo fileInfo, long offset, FuseReadDirFlags flags)
    {
        try
        {
            var remote = fileInfo.Context as RemoteFileEntry[] ?? core.List(PathOf(fileNamePtr));
            entries = RemoteFuseFileSystemCore.ReadDirectory(remote, offset)
                .Select(entry => new FuseDirEntry(entry.Name, entry.NextOffset, 0, ToFuseStat(entry.Entry)));
            return PosixResult.Success;
        }
        catch (Exception ex) { entries = []; return Map(ex); }
    }

    public PosixResult Open(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        try { return core.Stat(PathOf(fileNamePtr)).Directory ? PosixResult.EISDIR : PosixResult.Success; }
        catch (Exception ex) { return Map(ex); }
    }

    public PosixResult Read(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> buffer, long position,
        out int readLength, ref FuseFileInfo fileInfo)
    {
        try
        {
            var data = core.Read(PathOf(fileNamePtr), position, buffer.Length);
            data.CopyTo(buffer.Span);
            readLength = data.Length;
            return PosixResult.Success;
        }
        catch (Exception ex) { readLength = 0; return Map(ex); }
    }

    public PosixResult Write(ReadOnlyNativeMemory<byte> fileNamePtr, ReadOnlyNativeMemory<byte> buffer,
        long position, out int writtenLength, ref FuseFileInfo fileInfo)
    {
        try { writtenLength = core.Write(PathOf(fileNamePtr), position, buffer.Span); return PosixResult.Success; }
        catch (Exception ex) { writtenLength = 0; return Map(ex); }
    }

    public PosixResult Create(ReadOnlyNativeMemory<byte> fileNamePtr, int mode, ref FuseFileInfo fileInfo)
    {
        try { core.Create(PathOf(fileNamePtr), directory: false); return PosixResult.Success; }
        catch (Exception ex) { return Map(ex); }
    }

    public PosixResult MkDir(ReadOnlyNativeMemory<byte> fileNamePtr, PosixFileMode mode)
    {
        try { core.Create(PathOf(fileNamePtr), directory: true); return PosixResult.Success; }
        catch (Exception ex) { return Map(ex); }
    }

    public PosixResult Unlink(ReadOnlyNativeMemory<byte> fileNamePtr)
    {
        try { core.Delete(PathOf(fileNamePtr), directory: false); return PosixResult.Success; }
        catch (Exception ex) { return Map(ex); }
    }

    public PosixResult RmDir(ReadOnlyNativeMemory<byte> fileNamePtr)
    {
        try { core.Delete(PathOf(fileNamePtr), directory: true); return PosixResult.Success; }
        catch (Exception ex) { return Map(ex); }
    }

    public PosixResult Rename(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
    {
        try { core.Rename(PathOf(from), PathOf(to), replace: true); return PosixResult.Success; }
        catch (Exception ex) { return Map(ex); }
    }

    public PosixResult Truncate(ReadOnlyNativeMemory<byte> fileNamePtr, long size)
    {
        try { core.SetLength(PathOf(fileNamePtr), size); return PosixResult.Success; }
        catch (Exception ex) { return Map(ex); }
    }

    public PosixResult UTime(ReadOnlyNativeMemory<byte> fileNamePtr, TimeSpec atime, TimeSpec mtime,
        ref FuseFileInfo fileInfo)
    {
        try
        {
            core.SetTimes(PathOf(fileNamePtr), atime.IsOmit ? null : atime.ToDateTime().ToUnixTimeMilliseconds(),
                mtime.IsOmit ? null : mtime.ToDateTime().ToUnixTimeMilliseconds());
            return PosixResult.Success;
        }
        catch (Exception ex) { return Map(ex); }
    }

    public PosixResult Access(ReadOnlyNativeMemory<byte> fileNamePtr, PosixAccessMode mask)
    {
        try
        {
            var stat = core.Stat(PathOf(fileNamePtr));
            if ((mask & PosixAccessMode.Write) != 0 && (core.Volume.ReadOnly || stat.ReadOnly)) return PosixResult.EROFS;
            if ((mask & PosixAccessMode.Execute) != 0 && !stat.Directory) return PosixResult.EACCES;
            return PosixResult.Success;
        }
        catch (Exception ex) { return Map(ex); }
    }

    public PosixResult StatFs(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseVfsStat statvfs)
    {
        const ulong blockSize = 4096;
        var total = core.Volume.TotalBytes is > 0 ? (ulong)core.Volume.TotalBytes.Value : 0;
        var free = core.Volume.FreeBytes is > 0 ? (ulong)core.Volume.FreeBytes.Value : 0;
        statvfs = new FuseVfsStat
        {
            f_bsize = blockSize,
            f_frsize = blockSize,
            f_blocks = total == 0 ? 0 : (total + blockSize - 1) / blockSize,
            f_bfree = free == 0 ? 0 : free / blockSize,
            f_bavail = free == 0 ? 0 : free / blockSize,
            f_namemax = 255
        };
        return PosixResult.Success;
    }

    public void Init(ref FuseConnInfo fuse_conn_info)
    {
        // The wire protocol stores timestamps in Unix milliseconds.
        fuse_conn_info.time_gran = 1_000_000;
    }

    public PosixResult Flush(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        // Closing a read-only handle must not require write access on the remote file.
        const int accessModeMask = 3; // Linux O_ACCMODE; O_RDONLY is zero.
        if (((int)fileInfo.flags & accessModeMask) == 0) return PosixResult.Success;
        try
        {
            var path = PathOf(fileNamePtr);
            core.Flush(path, core.Stat(path).Directory);
            return PosixResult.Success;
        }
        catch (Exception ex) { return Map(ex); }
    }
    public PosixResult FSync(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => Flush(fileNamePtr, ref fileInfo);
    public PosixResult FSyncDir(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo)
    {
        try { core.Flush(PathOf(fileNamePtr), directory: true); return PosixResult.Success; }
        catch (Exception ex) { return Map(ex); }
    }
    public PosixResult Release(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => PosixResult.Success;
    public PosixResult ReleaseDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        fileInfo.Context = null;
        return PosixResult.Success;
    }
    public PosixResult ReadLink(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> target) => PosixResult.ENOSYS;
    public PosixResult SymLink(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.ENOSYS;
    public PosixResult Link(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.ENOSYS;
    public PosixResult ChMod(NativeMemory<byte> fileNamePtr, PosixFileMode mode) => PosixResult.ENOTSUP;
    public PosixResult ChOwn(NativeMemory<byte> fileNamePtr, int uid, int gid) => PosixResult.ENOTSUP;
    public PosixResult FAllocate(NativeMemory<byte> fileNamePtr, FuseAllocateMode mode, long offset, long length,
        ref FuseFileInfo fileInfo) => PosixResult.ENOSYS;
    public PosixResult IoCtl(ReadOnlyNativeMemory<byte> fileNamePtr, int cmd, nint arg, ref FuseFileInfo fileInfo,
        FuseIoctlFlags flags, nint data) => PosixResult.ENOSYS;
    public void Dispose() { }

    private static string PathOf(ReadOnlyNativeMemory<byte> path) => FuseHelper.GetString(path);

    private static FuseFileStat ToFuseStat(RemoteFileStat stat)
    {
        var time = new TimeSpec(stat.LastWriteUnixMs);
        var mode = stat.Directory
            ? PosixFileMode.Directory | PosixFileMode.OwnerAll | PosixFileMode.GroupReadExecute | PosixFileMode.OthersReadExecute
            : PosixFileMode.Regular | PosixFileMode.OwnerReadWrite | PosixFileMode.GroupRead | PosixFileMode.OthersRead;
        if (stat.ReadOnly)
            mode &= ~(PosixFileMode.OwnerWrite | PosixFileMode.GroupWrite | PosixFileMode.OthersWrite);
        return new FuseFileStat
        {
            st_mode = mode,
            st_nlink = stat.Directory ? 2 : 1,
            st_size = stat.Directory ? 0 : Math.Max(0, stat.Length),
            st_blksize = 4096,
            st_blocks = stat.Directory ? 0 : (Math.Max(0, stat.Length) + 511) / 512,
            st_uid = UserId,
            st_gid = GroupId,
            st_atim = time,
            st_mtim = time,
            st_ctim = time
        };
    }

    private FuseFileStat ToFuseStat(RemoteFileEntry entry) => ToFuseStat(new RemoteFileStat(entry.Name,
        entry.Directory, entry.Length, entry.LastWriteUnixMs, core.Volume.ReadOnly));

    private static PosixResult Map(Exception exception)
    {
        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            exception = aggregate.InnerException!;
        return exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => PosixResult.ENOENT,
            UnauthorizedAccessException => PosixResult.EROFS,
            TimeoutException or OperationCanceledException => TimedOut,
            ArgumentException or InvalidDataException => PosixResult.EINVAL,
            NotSupportedException or PlatformNotSupportedException => PosixResult.ENOTSUP,
            IOException io when io.Message.Contains("not empty", StringComparison.OrdinalIgnoreCase) => DirectoryNotEmpty,
            IOException io when io.Message.Contains("exists", StringComparison.OrdinalIgnoreCase) => PosixResult.EEXIST,
            IOException io when io.Message.Contains("directory", StringComparison.OrdinalIgnoreCase) => PosixResult.EISDIR,
            IOException => PosixResult.EIO,
            _ => PosixResult.EIO
        };
    }

    private static class NativeUser
    {
        [DllImport("libc", ExactSpelling = true)] public static extern uint geteuid();
        [DllImport("libc", ExactSpelling = true)] public static extern uint getegid();
    }
}
