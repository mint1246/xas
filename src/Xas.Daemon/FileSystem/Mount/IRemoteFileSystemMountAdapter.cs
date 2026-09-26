using Xas.Core.FileSystem;

namespace Xas.Daemon.FileSystem.Mount;

/// <summary>
/// Platform boundary for exposing a remote filesystem as a native mount. Implementations must bridge
/// filesystem callbacks to the remote fs.* protocol and stop callbacks before unmounting.
/// </summary>
public interface IRemoteFileSystemMountAdapter : IAsyncDisposable
{
    bool IsAvailable { get; }
    string PlatformName { get; }
    ValueTask MountAsync(string mountPoint, IRemoteFileSystemOperations remoteFileSystem, CancellationToken cancellationToken);
    ValueTask UnmountAsync(CancellationToken cancellationToken);
}

/// <summary>Operations that a native filesystem provider needs to forward to its remote peer.</summary>
public interface IRemoteFileSystemOperations
{
    ValueTask<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken);
    ValueTask<RemoteDirectoryPage> ListAsync(string path, int offset, CancellationToken cancellationToken);
    ValueTask<byte[]> ReadAsync(string path, long offset, int length, CancellationToken cancellationToken);
    ValueTask<int> WriteAsync(string path, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    ValueTask CreateAsync(string path, bool directory, bool replace, CancellationToken cancellationToken);
    ValueTask DeleteAsync(string path, bool directory, CancellationToken cancellationToken);
    ValueTask RenameAsync(string path, string newPath, bool replace, CancellationToken cancellationToken);
}

/// <summary>Explicit unavailable boundary used until a WinFsp or FUSE adapter is installed.</summary>
public sealed class UnavailableRemoteFileSystemMountAdapter(string platformName) : IRemoteFileSystemMountAdapter
{
    public bool IsAvailable => false;
    public string PlatformName { get; } = platformName;
    public ValueTask MountAsync(string mountPoint, IRemoteFileSystemOperations remoteFileSystem, CancellationToken cancellationToken) =>
        ValueTask.FromException(new PlatformNotSupportedException($"A {PlatformName} filesystem mount adapter is not installed."));
    public ValueTask UnmountAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
