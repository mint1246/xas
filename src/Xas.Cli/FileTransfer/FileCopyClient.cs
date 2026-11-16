using System.Buffers.Binary;
using System.Diagnostics;
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
    // Keep comfortably below the 1 MiB protocol frame ceiling while amortizing TLS, framing and IPC
    // overhead. 64 KiB made LAN transfers spend far too much time allocating and dispatching frames.
    private const int ChunkSize = 768 * 1024;
    private const int EarlyDownloadLimit = 1024 * 1024;
    private const int MaxPathLength = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ResolveTargetDeviceId(
        string source, string destination, LocalConfiguration config)
    {
        var sourceOperand = Operand.Parse(source, config);
        var destinationOperand = Operand.Parse(destination, config);
        if (sourceOperand.Remote == destinationOperand.Remote)
            throw new ArgumentException("Exactly one copy operand must be remote.");
        return (sourceOperand.Remote ? sourceOperand.Peer : destinationOperand.Peer)?.DeviceId
            ?? throw new InvalidOperationException("Copy operand does not resolve to a paired device.");
    }

    public static async Task CopyAsync(
        string source,
        string destination,
        bool recursive,
        bool overwrite,
        LocalConfiguration config,
        DeviceIdentity identity,
        PeerTrustStore trust,
        TextWriter progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(progress);
        token.ThrowIfCancellationRequested();
        var plan = CreateCopyPlan(source, destination, config);
        var peer = plan.Source.Remote ? plan.Source.Peer! : plan.Destination.Peer!;

        var connected = await ConnectAsync(peer, identity, trust, token).ConfigureAwait(false);
        await using (connected.ConfigureAwait(false))
        await using (var frames = new BinaryFrameConnection(connected.Stream, leaveOpen: true))
        await using (var protocol = new MultiplexedProtocolPeer(
            frames,
            (_, _) => ValueTask.FromException<ProtocolMessage>(
                new NotSupportedException("The file copy client does not accept requests."))))
        {
            await CopyResolvedAsync(
                protocol, plan, recursive, overwrite, progress, token).ConfigureAwait(false);
        }
    }

    public static async Task CopyWithPeerAsync(
        string source,
        string destination,
        bool recursive,
        bool overwrite,
        LocalConfiguration config,
        MultiplexedProtocolPeer protocol,
        TextWriter progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(progress);
        token.ThrowIfCancellationRequested();
        var plan = CreateCopyPlan(source, destination, config);
        await CopyResolvedAsync(protocol, plan, recursive, overwrite, progress, token)
            .ConfigureAwait(false);
    }

    private static CopyPlan CreateCopyPlan(
        string source, string destination, LocalConfiguration config)
    {
        var sourceOperand = Operand.Parse(source, config);
        var destinationOperand = Operand.Parse(destination, config);
        if (sourceOperand.Remote == destinationOperand.Remote)
            throw new ArgumentException("Exactly one copy operand must be remote.");

        var remotePath = ValidateRemotePath(
            sourceOperand.Remote ? sourceOperand.Path : destinationOperand.Path);
        var localSourcePath = sourceOperand.Remote
            ? destinationOperand.Path
            : sourceOperand.Path;
        var localPath = Path.GetFullPath(localSourcePath);
        ValidateLocalPath(localPath, allowMissing: sourceOperand.Remote);
        var sourceName = SafeBaseName(
            sourceOperand.Remote ? sourceOperand.Path : localPath);

        return new CopyPlan(
            sourceOperand, destinationOperand, remotePath, localPath, sourceName);
    }

    private static async Task CopyResolvedAsync(
        MultiplexedProtocolPeer protocol,
        CopyPlan plan,
        bool recursive,
        bool overwrite,
        TextWriter progress,
        CancellationToken token)
    {
        var destinationIsDirectory = plan.Source.Remote
            ? Directory.Exists(plan.LocalPath) || HasTrailingSeparator(plan.Destination.Path)
            : await IsRemoteDirectoryAsync(
                protocol,
                plan.RemotePath,
                HasTrailingSeparator(plan.Destination.Path),
                token).ConfigureAwait(false);
        if (plan.Source.Remote)
        {
            var stat = await StatAsync(protocol, plan.RemotePath, token).ConfigureAwait(false);
            var localPath = destinationIsDirectory
                ? Path.Combine(plan.LocalPath, plan.SourceName)
                : plan.LocalPath;
            if (stat.Kind == "directory")
            {
                if (!recursive) throw new IOException("Copying a directory requires recursive mode.");
                await DownloadDirectoryAsync(
                    protocol, plan.RemotePath, localPath, overwrite, progress, token)
                    .ConfigureAwait(false);
            }
            else
                await DownloadFileAsync(
                    protocol, plan.RemotePath, localPath, overwrite, progress, token)
                    .ConfigureAwait(false);
        }
        else
        {
            var attrs = File.GetAttributes(plan.LocalPath);
            var remotePath = destinationIsDirectory
                ? JoinRemote(plan.RemotePath, plan.SourceName)
                : plan.RemotePath;
            if ((attrs & FileAttributes.Directory) != 0)
            {
                if (!recursive) throw new IOException("Copying a directory requires recursive mode.");
                await UploadDirectoryAsync(
                    protocol, plan.LocalPath, remotePath, overwrite, progress, token)
                    .ConfigureAwait(false);
            }
            else
                await UploadFileAsync(
                    protocol, plan.LocalPath, remotePath, overwrite, progress, token)
                    .ConfigureAwait(false);
        }
    }

    private sealed record CopyPlan(
        Operand Source,
        Operand Destination,
        string RemotePath,
        string LocalPath,
        string SourceName);

    private static async Task UploadDirectoryAsync(
        MultiplexedProtocolPeer peer,
        string local,
        string remote,
        bool overwrite,
        TextWriter progress,
        CancellationToken token)
    {
        await RequestAsync(peer, "file.mkdir", new { path = remote }, token).ConfigureAwait(false);
        var entries = Directory.EnumerateFileSystemEntries(local)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal);
        foreach (var item in entries)
        {
            token.ThrowIfCancellationRequested();
            ValidateLocalPath(item, allowMissing: false);
            var child = JoinRemote(remote, Path.GetFileName(item));
            if ((File.GetAttributes(item) & FileAttributes.Directory) != 0)
            {
                await UploadDirectoryAsync(peer, item, child, overwrite, progress, token)
                    .ConfigureAwait(false);
                continue;
            }
            await UploadFileAsync(peer, item, child, overwrite, progress, token)
                .ConfigureAwait(false);
        }
    }

    private static async Task UploadFileAsync(
        MultiplexedProtocolPeer peer,
        string local,
        string remote,
        bool overwrite,
        TextWriter progress,
        CancellationToken token)
    {
        ValidateLocalPath(local, false);
        var modifiedAt = File.GetLastWriteTimeUtc(local);
        var lastWrite = new DateTimeOffset(modifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var openRequest = new { path = remote, overwrite, lastWriteUnixMs = lastWrite };
        var opened = await RequestAsync(peer, "file.put.open", openRequest, token)
            .ConfigureAwait(false);
        var id = ReadTransferId(opened.Payload);
        long sent = 0;
        var total = new FileInfo(local).Length;
        var reporter = new TransferProgress(progress, local, remote, total);
        try
        {
            await using var input = new FileStream(
                local, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[ChunkSize];
            while (true)
            {
                var n = await ReadBlockAsync(input, buffer, token).ConfigureAwait(false);
                if (n == 0) break;
                // BinaryFrameConnection copies the payload into its pooled wire buffer before SendAsync
                // completes, so a full read can reuse this same file buffer without another 768 KiB allocation.
                byte[] payload;
                if (n == buffer.Length)
                    payload = buffer;
                else
                    payload = buffer.AsSpan(0, n).ToArray();
                var dataMessage = new ProtocolMessage(
                    MessageKind.StreamData, 0, id, "file.put.data", payload);
                await peer.SendAsync(dataMessage, token).ConfigureAwait(false);
                sent += n;
                await reporter.UpdateAsync(sent, final: false).ConfigureAwait(false);
                if (n < buffer.Length) break;
            }
            await SendUploadEndAsync(peer, id, token).ConfigureAwait(false);
            var committed = await peer.RequestAsync(
                "file.put.commit", UInt32Payload(id), cancellationToken: token)
                .ConfigureAwait(false);
            var committedBytes = committed.Payload.Length == sizeof(long)
                ? BinaryPrimitives.ReadInt64BigEndian(committed.Payload)
                : -1;
            if (committedBytes != sent)
                throw new InvalidDataException("Remote file copy byte count did not match.");
            await reporter.UpdateAsync(sent, final: true).ConfigureAwait(false);
        }
        catch
        {
            await TrySendUploadEndAsync(peer, id).ConfigureAwait(false);
            throw;
        }
    }

    private static ValueTask SendUploadEndAsync(
        MultiplexedProtocolPeer peer, uint transferId, CancellationToken token)
    {
        var endMessage = new ProtocolMessage(
            MessageKind.StreamEnd, 0, transferId, "file.put.data", Array.Empty<byte>());
        return peer.SendAsync(endMessage, token);
    }

    private static async ValueTask<int> ReadBlockAsync(
        Stream input, byte[] buffer, CancellationToken token)
    {
        var bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            var count = await input.ReadAsync(buffer.AsMemory(bytesRead), token)
                .ConfigureAwait(false);
            if (count == 0) break;
            bytesRead += count;
        }
        return bytesRead;
    }

    private static async Task TrySendUploadEndAsync(
        MultiplexedProtocolPeer peer, uint transferId)
    {
        try
        {
            await SendUploadEndAsync(peer, transferId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The connection may already be gone; preserve the original transfer error.
        }
        catch (ObjectDisposedException)
        {
            // The connection may already be gone; preserve the original transfer error.
        }
    }

    private static async Task DownloadDirectoryAsync(
        MultiplexedProtocolPeer peer,
        string remote,
        string local,
        bool overwrite,
        TextWriter progress,
        CancellationToken token)
    {
        if (File.Exists(local)) throw new IOException($"Destination is a file: {local}");
        Directory.CreateDirectory(local);
        foreach (var entry in await ListAsync(peer, remote, token).ConfigureAwait(false))
        {
            if (!IsSafeEntryName(entry.Name))
                throw new InvalidDataException("Remote directory listing contains an invalid entry name.");
            var target = Path.Combine(local, entry.Name);
            ValidateLocalPath(target, true);
            var child = JoinRemote(remote, entry.Name);
            if (entry.Kind == "directory")
            {
                await DownloadDirectoryAsync(peer, child, target, overwrite, progress, token)
                    .ConfigureAwait(false);
                continue;
            }
            await DownloadFileAsync(peer, child, target, overwrite, progress, token)
                .ConfigureAwait(false);
        }
    }

    private static bool IsSafeEntryName(string name) =>
        !string.IsNullOrEmpty(name) &&
        name is not ("." or "..") &&
        name.IndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\', '\0']) < 0;

    private static void CancelIfActive(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The download finished before the connection completion continuation ran.
        }
    }

    private static async Task DownloadFileAsync(
        MultiplexedProtocolPeer peer,
        string remote,
        string local,
        bool overwrite,
        TextWriter progress,
        CancellationToken token)
    {
        ValidateLocalPath(local, true);
        var parent = Path.GetDirectoryName(local)
            ?? throw new IOException("Destination has no parent directory.");
        Directory.CreateDirectory(parent);
        if (File.Exists(local) && !overwrite) throw new IOException($"Destination already exists: {local}");
        var temp = Path.Combine(parent, ".xas-" + Guid.NewGuid().ToString("N") + ".tmp");
        var receive = new DownloadReceiver();
        peer.MessageReceived += receive.AcceptAsync;
        uint id = 0;
        try
        {
            var response = await RequestAsync(peer, "file.get.open", new { path = remote }, token)
                .ConfigureAwait(false);
            if (response.Payload.Length != 20)
                throw new InvalidDataException("Invalid file.get.open response.");
            id = BinaryPrimitives.ReadUInt32BigEndian(response.Payload.AsSpan(0, 4));
            if (id == 0) throw new InvalidDataException("Invalid transfer ID.");
            var length = BinaryPrimitives.ReadInt64BigEndian(response.Payload.AsSpan(4, 8));
            var lastWrite = BinaryPrimitives.ReadInt64BigEndian(response.Payload.AsSpan(12, 8));
            receive.Open(id);
            long count = 0;
            var reporter = new TransferProgress(progress, remote, local, length);
            await using (var output = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(token);
                _ = peer.Completion.ContinueWith(_ => CancelIfActive(disconnected),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                await foreach (var data in receive.ReadAllAsync(disconnected.Token).ConfigureAwait(false))
                {
                    await output.WriteAsync(data, token).ConfigureAwait(false);
                    count += data.Length;
                    await reporter.UpdateAsync(count, final: false).ConfigureAwait(false);
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            if (count != length)
                throw new InvalidDataException(
                    "Downloaded file length did not match the remote file.");
            File.SetLastWriteTimeUtc(temp, DateTimeOffset.FromUnixTimeMilliseconds(lastWrite).UtcDateTime);
            File.Move(temp, local, overwrite);
            await reporter.UpdateAsync(count, final: true).ConfigureAwait(false);
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
            finally
            {
                peer.MessageReceived -= receive.AcceptAsync;
                receive.Dispose();
            }
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

    private sealed class TransferProgress(TextWriter writer, string source, string destination, long total)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly bool _interactive = !Console.IsErrorRedirected;
        private long _lastReportTicks;
        private int _lastWidth;

        public async ValueTask UpdateAsync(long transferred, bool final)
        {
            var elapsed = _clock.Elapsed;
            if (!final && (!_interactive || elapsed.Ticks - _lastReportTicks < TimeSpan.FromMilliseconds(250).Ticks)) return;
            _lastReportTicks = elapsed.Ticks;
            if (!_interactive)
            {
                if (final) await writer.WriteLineAsync($"{source} -> {destination} ({transferred} bytes)").ConfigureAwait(false);
                return;
            }

            var seconds = Math.Max(0.001, elapsed.TotalSeconds);
            var rate = transferred / seconds;
            var percent = total > 0 ? Math.Clamp(transferred * 100d / total, 0, 100) : 100;
            var remainingSeconds = rate > 0 && total > transferred ? (total - transferred) / rate : 0;
            var eta = remainingSeconds > 0 ? $"  ETA {FormatDuration(remainingSeconds)}" : string.Empty;
            var line = $"{ShortName(source)} -> {ShortName(destination)}  {FormatBytes(transferred)} / {FormatBytes(total)}  {percent,5:0.0}%  {FormatBytes(rate)}/s{eta}";
            if (line.Length < _lastWidth) line = line.PadRight(_lastWidth);
            _lastWidth = Math.Max(_lastWidth, line.Length);
            await writer.WriteAsync("\r" + line).ConfigureAwait(false);
            if (final) await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        private static string ShortName(string path)
        {
            var trimmed = path.TrimEnd('/', '\\');
            var slash = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
            return slash >= 0 && slash + 1 < trimmed.Length ? trimmed[(slash + 1)..] : trimmed;
        }

        private static string FormatBytes(double bytes)
        {
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            var value = Math.Max(0, bytes);
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
        }

        private static string FormatDuration(double seconds)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds}s";
            return $"{Math.Max(1, (int)Math.Ceiling(span.TotalSeconds))}s";
        }
    }

    private sealed class DownloadReceiver : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<ProtocolMessage> _early = [];
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private uint _id;
        private int _earlyBytes;
        private bool _opened;
        private Exception? _error;

        public ValueTask AcceptAsync(ProtocolMessage message)
        {
            if (!IsValidTransferMessage(message))
                return Fail(new InvalidDataException("Unexpected transfer message."));

            lock (_gate)
            {
                return _opened
                    ? WriteOpenedMessage(message)
                    : BufferEarlyMessage(message);
            }
        }

        private ValueTask BufferEarlyMessage(ProtocolMessage message)
        {
            _earlyBytes += message.Payload.Length;
            if (_earlyBytes > EarlyDownloadLimit)
                return Fail(new InvalidDataException(
                    "Remote download exceeded the early data buffer limit."));
            _early.Add(message);
            return ValueTask.CompletedTask;
        }

        private ValueTask WriteOpenedMessage(ProtocolMessage message)
        {
            if (message.StreamId != _id)
                return Fail(new InvalidDataException("Unexpected download transfer ID."));
            return WriteMessage(message);
        }

        private static bool IsValidTransferMessage(ProtocolMessage message) =>
            message.Method == "file.get.data" &&
            message.Kind is MessageKind.StreamData or MessageKind.StreamEnd &&
            (message.Kind != MessageKind.StreamData || message.Payload.Length <= ChunkSize) &&
            (message.Kind != MessageKind.StreamEnd || message.Payload.Length == 0);

        private ValueTask Fail(Exception error)
        {
            _error = error;
            _channel.Writer.TryComplete(error);
            return ValueTask.CompletedTask;
        }

        private ValueTask WriteMessage(ProtocolMessage message)
        {
            if (message.Kind == MessageKind.StreamEnd)
                _channel.Writer.TryComplete();
            else
                _channel.Writer.TryWrite(message.Payload);
            return ValueTask.CompletedTask;
        }

        public void Open(uint id)
        {
            lock (_gate)
            {
                _id = id;
                _opened = true;
                foreach (var message in _early)
                {
                    if (message.StreamId != id)
                    {
                        _error = new InvalidDataException("Unexpected download transfer ID.");
                        break;
                    }
                    WriteMessage(message);
                }
                _early.Clear();
                if (_error is not null)
                    _channel.Writer.TryComplete(_error);
            }
        }

        public async IAsyncEnumerable<byte[]> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var chunk in _channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                yield return chunk;
        }

        public void Dispose() => _channel.Writer.TryComplete();
    }

    private sealed record Operand(bool Remote, string Path, ConfiguredPeer? Peer)
    {
        public static Operand Parse(string value, LocalConfiguration config)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value[0] == ':')
            {
                if (value.Length == 1) throw new ArgumentException("Default-device copy operand requires a path after ':'.", nameof(value));
                var defaultPeer = config.Resolve(null)
                    ?? throw new InvalidOperationException("No default device is configured; use <device>:<path> or run 'xas default <device>'.");
                return new(true, value[1..], defaultPeer);
            }
            var colon = value.IndexOf(':');
            if (colon <= 0 || (colon == 1 && char.IsLetter(value[0]) && value.Length > 2 && value[2] is '\\' or '/')) return new(false, value, null);
            var peer = config.Resolve(value[..colon]) ?? throw new InvalidOperationException($"No configured peer matches '{value[..colon]}'.");
            return new(true, value[(colon + 1)..], peer);
        }
    }
}
