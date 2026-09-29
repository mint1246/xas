using Xas.Core.FileSystem;
using Xas.Daemon.FileSystem.Mount;

namespace Xas.Tests;

public static class FuseCoreTests
{
    public static Task RunAsync()
    {
        ChunkedReadAndWritePreserveData();
        DirectoryPagingAndMutationsForwardCorrectly();
        FlushIsForwardedToRemotePeer();
        ReadOnlyVolumeRejectsMutation();
        return Task.CompletedTask;
    }

    private static void ChunkedReadAndWritePreserveData()
    {
        var remote = new RecordingOperations();
        remote.Data = Enumerable.Range(0, 150_000).Select(i => (byte)(i % 251)).ToArray();
        var core = new RemoteFuseFileSystemCore(new RemoteVolume("vol", "Data", "export", false), remote);

        var read = core.Read("/large.bin", 0, remote.Data.Length);
        Assert(read.SequenceEqual(remote.Data), "FUSE core changed data while reconstructing a chunked read.");
        Assert(remote.ReadRequests.Count == 3, "150 KiB read did not split into three bounded RPC requests.");
        Assert(remote.ReadRequests.All(r => r.Length <= remote.MaxTransferBytes),
            "FUSE core issued a read larger than the RPC chunk limit.");
        Equal(0L, remote.ReadRequests[0].Offset, "First read offset was wrong.");
        Equal((long)remote.MaxTransferBytes, remote.ReadRequests[1].Offset, "Second read offset was wrong.");

        var replacement = Enumerable.Range(0, 140_000).Select(i => (byte)(255 - i % 239)).ToArray();
        var written = core.Write("large.bin", 7, replacement);
        Equal(replacement.Length, written, "FUSE core reported the wrong chunked write size.");
        Assert(remote.WriteRequests.Count == 3, "140 KiB write did not split into three bounded RPC requests.");
        Assert(remote.WriteRequests.All(r => r.Data.Length <= remote.MaxTransferBytes),
            "FUSE core issued a write larger than the RPC chunk limit.");
        Equal(7L, remote.WriteRequests[0].Offset, "First write offset was wrong.");
        Equal(7L + remote.MaxTransferBytes, remote.WriteRequests[1].Offset, "Second write offset was wrong.");
    }

    private static void DirectoryPagingAndMutationsForwardCorrectly()
    {
        var remote = new RecordingOperations();
        var core = new RemoteFuseFileSystemCore(new RemoteVolume("vol", "Projects", "export", false), remote);

        var entries = core.List("/folder/");
        Assert(entries.Select(e => e.Name).SequenceEqual(["a.txt", "b", "c.txt"]),
            "FUSE core did not concatenate remote directory pages in order.");
        Assert(remote.ListOffsets.SequenceEqual([0, 2]), "FUSE core requested the wrong directory page offsets.");

        remote.ListOffsets.Clear();
        var first = core.ReadDirectory("/folder", 0);
        Assert(first.Select(e => e.Name).SequenceEqual([".", "..", "a.txt", "b", "c.txt"]),
            "FUSE directory view did not include dot entries in the expected order.");
        Assert(first.Select(e => e.NextOffset).SequenceEqual([1L, 2L, 3L, 4L, 5L]),
            "FUSE directory continuation offsets were not stable.");
        var resumed = core.ReadDirectory("/folder", 4);
        Assert(resumed.Select(e => e.Name).SequenceEqual(["c.txt"]),
            "FUSE directory continuation did not resume after the supplied offset.");
        Assert(core.ReadDirectory("/folder", 5).Length == 0,
            "FUSE directory continuation did not terminate at the final offset.");

        core.Create("/folder/new.txt", directory: false);
        core.Create("/folder/new-dir", directory: true);
        core.Rename("/folder/new.txt", "/folder/renamed.txt", replace: true);
        core.SetLength("/folder/renamed.txt", 123);
        core.SetTimes("/folder/renamed.txt", 1000, 2000);
        core.Delete("/folder/renamed.txt", directory: false);
        core.Delete("/folder/new-dir", directory: true);

        Assert(remote.Created.Contains(("folder/new.txt", false, false)), "File create path was not normalized/forwarded.");
        Assert(remote.Created.Contains(("folder/new-dir", true, false)), "Directory create was not forwarded.");
        Equal(("folder/new.txt", "folder/renamed.txt", true), remote.Renamed.Single(), "Rename request was wrong.");
        Equal(("folder/renamed.txt", 123L), remote.Lengths.Single(), "Truncate request was wrong.");
        Equal(("folder/renamed.txt", 1000L, 2000L), remote.Times.Single(), "Timestamp request was wrong.");
        Assert(remote.Deleted.Contains(("folder/renamed.txt", false)), "File delete was not forwarded.");
        Assert(remote.Deleted.Contains(("folder/new-dir", true)), "Directory delete was not forwarded.");
    }

    private static void ReadOnlyVolumeRejectsMutation()
    {
        var remote = new RecordingOperations();
        var core = new RemoteFuseFileSystemCore(new RemoteVolume("ro", "ReadOnly", "export", true), remote);
        try
        {
            core.Write("file.txt", 0, [1, 2, 3]);
            throw new InvalidOperationException("Read-only FUSE core unexpectedly allowed a write.");
        }
        catch (UnauthorizedAccessException) { }
        Equal(0, remote.WriteRequests.Count, "Read-only mutation reached the remote RPC backend.");
    }

    private static void FlushIsForwardedToRemotePeer()
    {
        var remote = new RecordingOperations();
        var core = new RemoteFuseFileSystemCore(new RemoteVolume("vol", "Data", "export", false), remote);
        core.Flush("/folder/data.bin");
        core.Flush("/folder", directory: true);
        Assert(remote.Flushes.SequenceEqual([("folder/data.bin", false), ("folder", true)]),
            "FUSE core did not forward file and directory durable flushes.");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingOperations : IRemoteFileSystemOperations
    {
        public int MaxTransferBytes { get; set; } = RemoteFileSystemWire.LegacyMaxChunkBytes;
        public byte[] Data { get; set; } = [];
        public List<(long Offset, int Length)> ReadRequests { get; } = [];
        public List<(long Offset, byte[] Data)> WriteRequests { get; } = [];
        public List<int> ListOffsets { get; } = [];
        public List<(string Path, bool Directory, bool Replace)> Created { get; } = [];
        public List<(string Path, bool Directory)> Deleted { get; } = [];
        public List<(string Path, string NewPath, bool Replace)> Renamed { get; } = [];
        public List<(string Path, long Length)> Lengths { get; } = [];
        public List<(string Path, long Access, long Write)> Times { get; } = [];
        public List<(string Path, bool Directory)> Flushes { get; } = [];

        public ValueTask<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RemoteFileStat(Path.GetFileName(path), false, Data.LongLength, 1234, false));

        public ValueTask<RemoteDirectoryPage> ListAsync(string path, int offset, CancellationToken cancellationToken)
        {
            ListOffsets.Add(offset);
            return ValueTask.FromResult(offset switch
            {
                0 => new RemoteDirectoryPage([
                    new RemoteFileEntry("a.txt", false, 1, 1),
                    new RemoteFileEntry("b", true, 0, 2)
                ], true),
                2 => new RemoteDirectoryPage([new RemoteFileEntry("c.txt", false, 3, 3)], false),
                _ => throw new InvalidOperationException("Unexpected list offset.")
            });
        }

        public ValueTask<byte[]> ReadAsync(string path, long offset, int length, CancellationToken cancellationToken)
        {
            ReadRequests.Add((offset, length));
            if (offset >= Data.LongLength) return ValueTask.FromResult(Array.Empty<byte>());
            var count = checked((int)Math.Min(length, Data.LongLength - offset));
            return ValueTask.FromResult(Data.AsSpan(checked((int)offset), count).ToArray());
        }

        public ValueTask<int> WriteAsync(string path, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            WriteRequests.Add((offset, data.ToArray()));
            return ValueTask.FromResult(data.Length);
        }

        public ValueTask CreateAsync(string path, bool directory, bool replace, CancellationToken cancellationToken)
        { Created.Add((path, directory, replace)); return ValueTask.CompletedTask; }

        public ValueTask DeleteAsync(string path, bool directory, CancellationToken cancellationToken)
        { Deleted.Add((path, directory)); return ValueTask.CompletedTask; }

        public ValueTask RenameAsync(string path, string newPath, bool replace, CancellationToken cancellationToken)
        { Renamed.Add((path, newPath, replace)); return ValueTask.CompletedTask; }

        public ValueTask SetLengthAsync(string path, long length, CancellationToken cancellationToken)
        { Lengths.Add((path, length)); return ValueTask.CompletedTask; }

        public ValueTask SetInfoAsync(string path, long? creationUnixMs, long? lastAccessUnixMs,
            long? lastWriteUnixMs, bool? readOnly, CancellationToken cancellationToken)
        {
            Times.Add((path, lastAccessUnixMs ?? -1, lastWriteUnixMs ?? -1));
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(string path, bool directory, CancellationToken cancellationToken)
        { Flushes.Add((path, directory)); return ValueTask.CompletedTask; }
    }
}
