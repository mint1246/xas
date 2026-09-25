using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Transport;

namespace Xas.Cli.FileTransfer;

/// <summary>Copies files and directory trees between this machine and one configured XAS peer.</summary>
public static class FileCopyClient
{
    private const int ChunkSize = 64 * 1024;
    private const int EarlyDownloadLimit = 1024 * 1024;
    private const int MaxPathLength = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task CopyAsync(string source, string destination, bool recursive, bool overwrite,
        LocalConfiguration config, DeviceIdentity identity, PeerTrustStore trust, TextWriter progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(progress);
        token.ThrowIfCancellationRequested();
        var from = Operand.Parse(source, config);
        var to = Operand.Parse(destination, config);
        if (from.Remote == to.Remote) throw new ArgumentException("Exactly one copy operand must be remote.");
        var peer = from.Remote ? from.Peer! : to.Peer!;
        var remotePath = ValidateRemotePath(from.Remote ? from.Path : to.Path);
        var localPath = Path.GetFullPath(from.Remote ? to.Path : from.Path);
        ValidateLocalPath(localPath, allowMissing: from.Remote);
        var sourceName = SafeBaseName(from.Remote ? from.Path : Path.GetFullPath(from.Path));

        var connected = await ConnectAsync(peer!, identity, trust, token).ConfigureAwait(false);
        await using (connected.ConfigureAwait(false))
        await using (var frames = new BinaryFrameConnection(connected.Stream, leaveOpen: true))
        await using (var protocol = new MultiplexedProtocolPeer(frames, (_, _) =>
            ValueTask.FromException<ProtocolMessage>(new NotSupportedException("The file copy client does not accept requests."))))
        {
            var destinationIsDirectory = from.Remote
                ? Directory.Exists(localPath) || HasTrailingSeparator(to.Path)
                : await IsRemoteDirectoryAsync(protocol, remotePath, HasTrailingSeparator(to.Path), token).ConfigureAwait(false);
            if (from.Remote)
            {
                var stat = await StatAsync(protocol, remotePath, token).ConfigureAwait(false);
                if (destinationIsDirectory) localPath = Path.Combine(localPath, sourceName);
                if (stat.Kind == "directory")
                {
                    if (!recursive) throw new IOException("Copying a directory requires recursive mode.");
                    await DownloadDirectoryAsync(protocol, remotePath, localPath, overwrite, progress, token).ConfigureAwait(false);
                }
                else await DownloadFileAsync(protocol, remotePath, localPath, overwrite, progress, token).ConfigureAwait(false);
            }
            else
            {
                var attrs = File.GetAttributes(localPath);
                if (destinationIsDirectory) remotePath = JoinRemote(remotePath, sourceName);
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if (!recursive) throw new IOException("Copying a directory requires recursive mode.");
                    await UploadDirectoryAsync(protocol, localPath, remotePath, overwrite, progress, token).ConfigureAwait(false);
                }
                else await UploadFileAsync(protocol, localPath, remotePath, overwrite, progress, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task UploadDirectoryAsync(MultiplexedProtocolPeer peer, string local, string remote,
        bool overwrite, TextWriter progress, CancellationToken token)
    {
        await RequestAsync(peer, "file.mkdir", new { path = remote }, token).ConfigureAwait(false);
        foreach (var item in Directory.EnumerateFileSystemEntries(local).OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested(); ValidateLocalPath(item, allowMissing: false);
            var child = JoinRemote(remote, Path.GetFileName(item));
            if ((File.GetAttributes(item) & FileAttributes.Directory) != 0)
                await UploadDirectoryAsync(peer, item, child, overwrite, progress, token).ConfigureAwait(false);
            else await UploadFileAsync(peer, item, child, overwrite, progress, token).ConfigureAwait(false);
        }
    }

    private static async Task UploadFileAsync(MultiplexedProtocolPeer peer, string local, string remote,
        bool overwrite, TextWriter progress, CancellationToken token)
    {
        ValidateLocalPath(local, false);
        var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(local), TimeSpan.Zero).ToUnixTimeMilliseconds();
        var opened = await RequestAsync(peer, "file.put.open", new { path = remote, overwrite, lastWriteUnixMs = lastWrite }, token).ConfigureAwait(false);
        var id = ReadTransferId(opened.Payload);
        long sent = 0;
        try
        {
            await using var input = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[ChunkSize];
            while (true)
            {
                var n = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (n == 0) break;
                await peer.SendAsync(new ProtocolMessage(MessageKind.StreamData, 0, id, "file.put.data", buffer.AsSpan(0, n).ToArray()), token).ConfigureAwait(false);
                sent += n;
            }
            await peer.SendAsync(new ProtocolMessage(MessageKind.StreamEnd, 0, id, "file.put.data", Array.Empty<byte>()), token).ConfigureAwait(false);
            var committed = await peer.RequestAsync("file.put.commit", UInt32Payload(id), cancellationToken: token).ConfigureAwait(false);
            if (committed.Payload.Length != 8 || BinaryPrimitives.ReadInt64BigEndian(committed.Payload) != sent)
                throw new InvalidDataException("Remote file copy byte count did not match.");
            await progress.WriteLineAsync($"{local} -> {remote} ({sent} bytes)").ConfigureAwait(false);
        }
        catch
        {
            try { await peer.SendAsync(new ProtocolMessage(MessageKind.StreamEnd, 0, id, "file.put.data", Array.Empty<byte>()), CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static async Task DownloadDirectoryAsync(MultiplexedProtocolPeer peer, string remote, string local,
        bool overwrite, TextWriter progress, CancellationToken token)
    {
        if (File.Exists(local)) throw new IOException($"Destination is a file: {local}");
        Directory.CreateDirectory(local);
        foreach (var entry in await ListAsync(peer, remote, token).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.Name is "." or ".." ||
                entry.Name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\', '\0']) >= 0)
                throw new InvalidDataException("Remote directory listing contains an invalid entry name.");
            var target = Path.Combine(local, entry.Name); ValidateLocalPath(target, true);
            var child = JoinRemote(remote, entry.Name);
            if (entry.Kind == "directory") await DownloadDirectoryAsync(peer, child, target, overwrite, progress, token).ConfigureAwait(false);
            else await DownloadFileAsync(peer, child, target, overwrite, progress, token).ConfigureAwait(false);
        }
    }

    private static async Task DownloadFileAsync(MultiplexedProtocolPeer peer, string remote, string local,
        bool overwrite, TextWriter progress, CancellationToken token)
    {
        ValidateLocalPath(local, true);
        var parent = Path.GetDirectoryName(local) ?? throw new IOException("Destination has no parent directory.");
        Directory.CreateDirectory(parent);
        if (File.Exists(local) && !overwrite) throw new IOException($"Destination already exists: {local}");
        var temp = Path.Combine(parent, ".xas-" + Guid.NewGuid().ToString("N") + ".tmp");
        var receive = new DownloadReceiver();
        peer.MessageReceived += receive.AcceptAsync;
        uint id = 0;
        try
        {
            var response = await RequestAsync(peer, "file.get.open", new { path = remote }, token).ConfigureAwait(false);
            if (response.Payload.Length != 20) throw new InvalidDataException("Invalid file.get.open response.");
            id = BinaryPrimitives.ReadUInt32BigEndian(response.Payload.AsSpan(0, 4));
            if (id == 0) throw new InvalidDataException("Invalid transfer ID.");
            var length = BinaryPrimitives.ReadInt64BigEndian(response.Payload.AsSpan(4, 8));
            var lastWrite = BinaryPrimitives.ReadInt64BigEndian(response.Payload.AsSpan(12, 8));
            receive.Open(id);
            long count = 0;
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(token);
                _ = peer.Completion.ContinueWith(_ => { try { disconnected.Cancel(); } catch (ObjectDisposedException) { } },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                await foreach (var data in receive.ReadAllAsync(disconnected.Token).ConfigureAwait(false))
                {
                    await output.WriteAsync(data, token).ConfigureAwait(false); count += data.Length;
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            if (count != length) throw new InvalidDataException("Downloaded file length did not match the remote file.");
            File.SetLastWriteTimeUtc(temp, DateTimeOffset.FromUnixTimeMilliseconds(lastWrite).UtcDateTime);
            File.Move(temp, local, overwrite);
            await progress.WriteLineAsync($"{remote} -> {local} ({count} bytes)").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && peer.Completion.IsCompleted)
        {
            if (id != 0) { try { await peer.RequestAsync("file.get.close", UInt32Payload(id), cancellationToken: CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { } }
            try { File.Delete(temp); } catch { }
            throw new IOException("The remote connection closed during the file download.");
        }
        catch
        {
            if (id != 0) { try { await peer.RequestAsync("file.get.close", UInt32Payload(id), cancellationToken: CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { } }
            try { File.Delete(temp); } catch { }
            throw;
        }
        finally { peer.MessageReceived -= receive.AcceptAsync; receive.Dispose(); }
    }

    private static async Task<FileStat> StatAsync(MultiplexedProtocolPeer peer, string path, CancellationToken token)
    {
        var message = await RequestAsync(peer, "file.stat", new { path }, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<FileStat>(message.Payload, JsonOptions) ?? throw new InvalidDataException("Invalid file.stat response.");
    }

    private static async Task<bool> IsRemoteDirectoryAsync(MultiplexedProtocolPeer peer, string path, bool trailingSeparator, CancellationToken token)
    {
        if (trailingSeparator) return true;
        try { return (await StatAsync(peer, path, token).ConfigureAwait(false)).Kind == "directory"; }
        catch (RemoteProtocolException) { return false; }
    }

    private static async Task<FileEntry[]> ListAsync(MultiplexedProtocolPeer peer, string path, CancellationToken token)
    {
        var all = new List<FileEntry>(); var offset = 0;
        while (true)
        {
            var message = await RequestAsync(peer, "file.list", new { path, offset }, token).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<FilePage>(message.Payload, JsonOptions) ?? throw new InvalidDataException("Invalid file.list response.");
            if (page.Entries.Length > 128) throw new InvalidDataException("Remote directory page exceeds its limit.");
            all.AddRange(page.Entries); offset += page.Entries.Length;
            if (!page.HasMore) return all.ToArray();
            if (page.Entries.Length == 0 || all.Count > 1_000_000) throw new InvalidDataException("Invalid or excessive remote directory listing.");
        }
    }

    private static async Task<ProtocolMessage> RequestAsync(MultiplexedProtocolPeer peer, string method, object payload, CancellationToken token) =>
        await peer.RequestAsync(method, JsonSerializer.SerializeToUtf8Bytes(payload), cancellationToken: token).ConfigureAwait(false);

    private static async Task<AuthenticatedPeerConnection> ConnectAsync(ConfiguredPeer peer, DeviceIdentity identity, PeerTrustStore trust, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await MutualTlsTransport.ConnectAsync(peer.Host, peer.Port, identity, trust, peer.DeviceId, TimeSpan.FromSeconds(10), timeout.Token).ConfigureAwait(false);
    }

    private static uint ReadTransferId(byte[] data)
    {
        if (data.Length != 4) throw new InvalidDataException("Invalid transfer ID response.");
        var id = BinaryPrimitives.ReadUInt32BigEndian(data); return id == 0 ? throw new InvalidDataException("Invalid transfer ID.") : id;
    }
    private static byte[] UInt32Payload(uint id) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, id); return bytes; }
    private static string ValidateRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength || path.IndexOf('\0') >= 0) throw new ArgumentException("Invalid remote path.");
        return path;
    }
    private static string JoinRemote(string root, string name) => ValidateRemotePath(root.TrimEnd('/', '\\') + "/" + name);
    private static bool HasTrailingSeparator(string path) => path.EndsWith('/') || path.EndsWith('\\');
    private static string SafeBaseName(string path)
    {
        var normalized = path.TrimEnd('/', '\\');
        var name = normalized[(Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('\\')) + 1)..];
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 ||
            name.IndexOfAny(['/', '\\', '\0']) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("Copy source must have a safe final path component.");
        return name;
    }
    private static void ValidateLocalPath(string path, bool allowMissing)
    {
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!; var current = root;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) { if (!allowMissing) throw new FileNotFoundException("Local copy source does not exist.", current); continue; }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException($"Symbolic links and reparse points are not supported: {current}");
        }
    }

    private sealed record FileStat(string Kind, long Length, long LastWriteUnixMs);
    private sealed record FileEntry(string Name, string Kind, long Length, long LastWriteUnixMs);
    private sealed record FilePage(FileEntry[] Entries, bool HasMore);

    private sealed class DownloadReceiver : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<ProtocolMessage> _early = [];
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private uint _id; private int _earlyBytes; private bool _opened; private Exception? _error;
        public ValueTask AcceptAsync(ProtocolMessage message)
        {
            if (message.Method != "file.get.data" || message.Kind is not (MessageKind.StreamData or MessageKind.StreamEnd) ||
                (message.Kind == MessageKind.StreamData && message.Payload.Length > ChunkSize) ||
                (message.Kind == MessageKind.StreamEnd && message.Payload.Length != 0))
            { _error = new InvalidDataException("Unexpected transfer message."); _channel.Writer.TryComplete(_error); return ValueTask.CompletedTask; }
            lock (_gate)
            {
                if (!_opened)
                {
                    _earlyBytes += message.Payload.Length;
                    if (_earlyBytes > EarlyDownloadLimit) { _error = new InvalidDataException("Remote download exceeded the early data buffer limit."); _channel.Writer.TryComplete(_error); return ValueTask.CompletedTask; }
                    _early.Add(message);
                    return ValueTask.CompletedTask;
                }
                if (message.StreamId != _id)
                {
                    _error = new InvalidDataException("Unexpected download transfer ID.");
                    _channel.Writer.TryComplete(_error);
                    return ValueTask.CompletedTask;
                }
                if (message.Kind == MessageKind.StreamEnd) _channel.Writer.TryComplete();
                else _channel.Writer.TryWrite(message.Payload);
                return ValueTask.CompletedTask;
            }
        }
        public void Open(uint id)
        {
            lock (_gate)
            {
                _id = id; _opened = true;
                foreach (var message in _early)
                {
                    if (message.StreamId != id) { _error = new InvalidDataException("Unexpected download transfer ID."); break; }
                    if (message.Kind == MessageKind.StreamEnd) _channel.Writer.TryComplete();
                    else _channel.Writer.TryWrite(message.Payload);
                }
                _early.Clear(); if (_error is not null) _channel.Writer.TryComplete(_error);
            }
        }
        public async IAsyncEnumerable<byte[]> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        { await foreach (var chunk in _channel.Reader.ReadAllAsync(token).ConfigureAwait(false)) yield return chunk; }
        public void Dispose() => _channel.Writer.TryComplete();
    }

    private sealed record Operand(bool Remote, string Path, ConfiguredPeer? Peer)
    {
        public static Operand Parse(string value, LocalConfiguration config)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var colon = value.IndexOf(':');
            if (colon <= 0 || (colon == 1 && char.IsLetter(value[0]) && value.Length > 2 && value[2] is '\\' or '/')) return new(false, value, null);
            var peer = config.Resolve(value[..colon]) ?? throw new InvalidOperationException($"No configured peer matches '{value[..colon]}'.");
            return new(true, value[(colon + 1)..], peer);
        }
    }
}
