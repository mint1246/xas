using System.Runtime.InteropServices;
using System.Text;
using Xas.Core.FileSystem;
using Xas.Daemon.FileSystem.Mount;

namespace Xas.Tests;

public static class WinFspAdapterTests
{
    public static Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        var remote = new MemoryRemoteFileSystem();
        remote.AddFile("hello.txt", Encoding.UTF8.GetBytes("hello"));
        remote.AddDirectory("folder");
        remote.AddFile("folder/a.txt", Encoding.UTF8.GetBytes("A"));
        var volume = new RemoteVolume("volume-test", "Remote SD", "removable", false,
            1024 * 1024, 768 * 1024, "exfat");
        var fs = new RemoteWinFspFileSystem(volume, remote);

        var status = fs.Open("\\hello.txt", 0, 0, out _, out var helloHandle, out var helloInfo, out _);
        Equal(0, status, "Open failed.");
        Equal<ulong>(5, helloInfo.FileSize, "Open returned the wrong size.");

        var buffer = Marshal.AllocHGlobal(32);
        try
        {
            status = fs.Read(null!, helloHandle, buffer, 0, 5, out var read);
            Equal(0, status, "Read failed.");
            Equal<uint>(5, read, "Read returned the wrong byte count.");
            var readBytes = new byte[read];
            Marshal.Copy(buffer, readBytes, 0, readBytes.Length);
            Equal("hello", Encoding.UTF8.GetString(readBytes), "Read returned the wrong bytes.");

            var suffix = Encoding.UTF8.GetBytes(" world");
            Marshal.Copy(suffix, 0, buffer, suffix.Length);
            status = fs.Write(null!, helloHandle, buffer, 5, checked((uint)suffix.Length), false, false,
                out var written, out var afterWrite);
            Equal(0, status, "Write failed.");
            Equal<uint>((uint)suffix.Length, written, "Write returned the wrong byte count.");
            Equal("hello world", Encoding.UTF8.GetString(remote.GetFile("hello.txt")), "Write did not reach the remote backend.");
            Equal<ulong>(11, afterWrite.FileSize, "Write returned stale file metadata.");
        }
        finally { Marshal.FreeHGlobal(buffer); }

        status = fs.SetFileSize(null!, helloHandle, 5, false, out var truncated);
        Equal(0, status, "SetFileSize failed.");
        Equal<ulong>(5, truncated.FileSize, "SetFileSize returned stale metadata.");
        Equal("hello", Encoding.UTF8.GetString(remote.GetFile("hello.txt")), "Truncate did not reach the remote backend.");

        status = fs.Rename(null!, helloHandle, "\\hello.txt", "\\renamed.txt", false);
        Equal(0, status, "Rename failed.");
        Assert(!remote.Exists("hello.txt") && remote.Exists("renamed.txt"), "Rename did not update the remote backend.");

        status = fs.Create("\\new.txt", 0, 0, 0, [], 0,
            out _, out var newHandle, out _, out _);
        Equal(0, status, "Create failed.");
        Assert(remote.Exists("new.txt"), "Create did not reach the remote backend.");
        status = fs.SetDelete(null!, newHandle, "\\new.txt", true);
        Equal(0, status, "SetDelete failed.");
        fs.Cleanup(null!, newHandle, "\\new.txt", 0);
        Assert(!remote.Exists("new.txt"), "Delete-on-cleanup did not reach the remote backend.");

        status = fs.Create("\\created-dir", 1 /* FILE_DIRECTORY_FILE */, 0, 0, [], 0,
            out _, out var dirHandle, out var dirInfo, out _);
        Equal(0, status, "Directory create failed.");
        Assert(remote.IsDirectory("created-dir"), "Directory create did not reach the remote backend.");
        Assert((((FileAttributes)dirInfo.FileAttributes) & FileAttributes.Directory) != 0,
            "Directory create did not report the directory attribute.");

        status = fs.Open("\\", 1 /* FILE_DIRECTORY_FILE */, 0, out _, out var rootHandle, out _, out _);
        Equal(0, status, "Root directory open failed.");
        object context = null!;
        var names = new List<string>();
        while (fs.ReadDirectoryEntry(null!, rootHandle, "*", null!, ref context, out var name, out _)) names.Add(name);
        Assert(names.Contains("renamed.txt", StringComparer.OrdinalIgnoreCase), "Directory enumeration missed renamed.txt.");
        Assert(names.Contains("folder", StringComparer.OrdinalIgnoreCase), "Directory enumeration missed folder.");
        Assert(names.Contains("created-dir", StringComparer.OrdinalIgnoreCase), "Directory enumeration missed created-dir.");

        status = fs.CanDelete(null!, dirHandle, "\\created-dir");
        Equal(0, status, "Empty directory should be deletable.");
        fs.SetDelete(null!, dirHandle, "\\created-dir", true);
        fs.Cleanup(null!, dirHandle, "\\created-dir", 0);
        Assert(!remote.Exists("created-dir"), "Directory delete did not reach the remote backend.");

        return Task.CompletedTask;
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

    private sealed class MemoryRemoteFileSystem : IRemoteFileSystemOperations
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase)
        {
            [""] = new Entry(true, [], false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        };

        public void AddDirectory(string path)
        {
            lock (_gate) _entries[Normalize(path)] = new Entry(true, [], false, Now());
        }

        public void AddFile(string path, byte[] data)
        {
            lock (_gate) _entries[Normalize(path)] = new Entry(false, data.ToArray(), false, Now());
        }

        public byte[] GetFile(string path)
        {
            lock (_gate) return _entries[Normalize(path)].Data.ToArray();
        }

        public bool Exists(string path) { lock (_gate) return _entries.ContainsKey(Normalize(path)); }
        public bool IsDirectory(string path) { lock (_gate) return _entries[Normalize(path)].Directory; }

        public ValueTask<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var normalized = Normalize(path);
                if (!_entries.TryGetValue(normalized, out var entry)) throw new FileNotFoundException(normalized);
                return ValueTask.FromResult(new RemoteFileStat(Path.GetFileName(normalized), entry.Directory,
                    entry.Directory ? 0 : entry.Data.LongLength, entry.LastWriteUnixMs, entry.ReadOnly));
            }
        }

        public ValueTask<RemoteDirectoryPage> ListAsync(string path, int offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var parent = Normalize(path);
                if (!_entries.TryGetValue(parent, out var parentEntry) || !parentEntry.Directory)
                    throw new DirectoryNotFoundException(parent);
                var prefix = parent.Length == 0 ? "" : parent + "/";
                var children = _entries.Where(pair => pair.Key.Length > prefix.Length && pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Where(pair => !pair.Key[prefix.Length..].Contains('/'))
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new RemoteFileEntry(pair.Key[prefix.Length..], pair.Value.Directory,
                        pair.Value.Directory ? 0 : pair.Value.Data.LongLength, pair.Value.LastWriteUnixMs))
                    .Skip(offset).Take(512).ToArray();
                return ValueTask.FromResult(new RemoteDirectoryPage(children, false));
            }
        }

        public ValueTask<byte[]> ReadAsync(string path, long offset, int length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var entry = File(path);
                if (offset >= entry.Data.LongLength) return ValueTask.FromResult(Array.Empty<byte>());
                var count = checked((int)Math.Min(length, entry.Data.LongLength - offset));
                return ValueTask.FromResult(entry.Data.AsSpan(checked((int)offset), count).ToArray());
            }
        }

        public ValueTask<int> WriteAsync(string path, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var key = Normalize(path);
                var entry = File(key);
                if (entry.ReadOnly) throw new UnauthorizedAccessException();
                var required = checked((int)(offset + data.Length));
                if (entry.Data.Length < required) Array.Resize(ref entry.Data, required);
                data.Span.CopyTo(entry.Data.AsSpan(checked((int)offset)));
                entry.LastWriteUnixMs = Now();
                _entries[key] = entry;
                return ValueTask.FromResult(data.Length);
            }
        }

        public ValueTask CreateAsync(string path, bool directory, bool replace, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var key = Normalize(path);
                if (_entries.ContainsKey(key) && !replace) throw new IOException("Destination exists.");
                RequireParent(key);
                _entries[key] = new Entry(directory, [], false, Now());
                return ValueTask.CompletedTask;
            }
        }

        public ValueTask DeleteAsync(string path, bool directory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var key = Normalize(path);
                if (!_entries.TryGetValue(key, out var entry)) throw new FileNotFoundException(key);
                if (entry.Directory != directory) throw new IOException("Type mismatch.");
                var prefix = key + "/";
                if (directory && _entries.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Directory not empty.");
                _entries.Remove(key);
                return ValueTask.CompletedTask;
            }
        }

        public ValueTask RenameAsync(string path, string newPath, bool replace, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var source = Normalize(path);
                var destination = Normalize(newPath);
                if (!_entries.TryGetValue(source, out var entry)) throw new FileNotFoundException(source);
                if (_entries.ContainsKey(destination) && !replace) throw new IOException("Destination exists.");
                RequireParent(destination);
                _entries.Remove(destination);
                _entries.Remove(source);
                _entries[destination] = entry;
                if (entry.Directory)
                {
                    foreach (var child in _entries.Where(p => p.Key.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
                    {
                        _entries.Remove(child.Key);
                        _entries[destination + child.Key[source.Length..]] = child.Value;
                    }
                }
                return ValueTask.CompletedTask;
            }
        }

        public ValueTask SetLengthAsync(string path, long length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var key = Normalize(path);
                var entry = File(key);
                Array.Resize(ref entry.Data, checked((int)length));
                entry.LastWriteUnixMs = Now();
                _entries[key] = entry;
                return ValueTask.CompletedTask;
            }
        }

        public ValueTask SetInfoAsync(string path, long? creationUnixMs, long? lastAccessUnixMs,
            long? lastWriteUnixMs, bool? readOnly, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var key = Normalize(path);
                if (!_entries.TryGetValue(key, out var entry)) throw new FileNotFoundException(key);
                if (lastWriteUnixMs is not null) entry.LastWriteUnixMs = lastWriteUnixMs.Value;
                if (readOnly is not null) entry.ReadOnly = readOnly.Value;
                _entries[key] = entry;
                return ValueTask.CompletedTask;
            }
        }

        private Entry File(string path)
        {
            var key = Normalize(path);
            if (!_entries.TryGetValue(key, out var entry)) throw new FileNotFoundException(key);
            if (entry.Directory) throw new IOException("File is a directory.");
            return entry;
        }

        private void RequireParent(string path)
        {
            var slash = path.LastIndexOf('/');
            var parent = slash < 0 ? "" : path[..slash];
            if (!_entries.TryGetValue(parent, out var entry) || !entry.Directory) throw new DirectoryNotFoundException(parent);
        }

        private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private sealed class Entry(bool directory, byte[] data, bool readOnly, long lastWriteUnixMs)
        {
            public bool Directory { get; } = directory;
            public byte[] Data = data;
            public bool ReadOnly = readOnly;
            public long LastWriteUnixMs = lastWriteUnixMs;
        }
    }
}
