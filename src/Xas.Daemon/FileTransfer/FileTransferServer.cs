using System.Buffers.Binary;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Security;

namespace Xas.Daemon.FileTransfer;

/// <summary>Handles explicit file transfer RPCs for one authenticated connection.</summary>
public sealed class FileTransferServer : IAsyncDisposable
{
    private const int MaxTransfers = 8;
    private const int MaxFrameBytes = 768 * 1024;
    private const int MaxPathChars = 4096;
    private const int MaxListEntries = 128;
    private const int MaxJsonBytes = 900 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _peerId;
    private readonly PeerPermissionStore _permissions;
    private readonly Func<ProtocolMessage, CancellationToken, ValueTask> _send;
    private readonly object _gate = new();
    private readonly Dictionary<uint, Transfer> _transfers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private ulong _nextId = 1;
    private int _opening;
    private bool _disposed;

    public FileTransferServer(string peerId, PeerPermissionStore permissions,
        Func<ProtocolMessage, CancellationToken, ValueTask> send)
    {
        _peerId = peerId ?? throw new ArgumentNullException(nameof(peerId));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    public ValueTask<ProtocolMessage> HandleRequestAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        return request.Method switch
        {
            "file.stat" => ValueTask.FromResult(Stat(request)),
            "file.list" => ValueTask.FromResult(List(request)),
            "file.mkdir" => ValueTask.FromResult(Mkdir(request)),
            "file.put.open" => OpenUploadAsync(request, cancellationToken),
            "file.put.commit" => CommitUploadAsync(request, cancellationToken),
            "file.get.open" => OpenDownloadAsync(request, cancellationToken),
            "file.get.close" => CloseDownloadAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"Unknown file transfer request: {request.Method}")
        };
    }

    public async ValueTask HandleMessageAsync(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        if (message.RequestId != 0 || message.StreamId == 0 || message.Method != "file.put.data")
            throw new InvalidDataException("Invalid file upload stream message.");
        var transfer = GetTransfer(message.StreamId, TransferKind.Upload);
        await transfer.Lock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        var failed = false;
        try
        {
            if (transfer.Finished) throw new InvalidDataException("Upload stream has already ended.");
            switch (message.Kind)
            {
                case MessageKind.StreamData:
                    if (message.Payload.Length is 0 or > MaxFrameBytes)
                        throw new InvalidDataException($"Upload frames must contain 1 to {MaxFrameBytes} bytes.");
                    await transfer.Stream!.WriteAsync(message.Payload, _shutdown.Token).ConfigureAwait(false);
                    transfer.Bytes = checked(transfer.Bytes + message.Payload.Length);
                    break;
                case MessageKind.StreamEnd:
                    if (message.Payload.Length != 0) throw new InvalidDataException("Upload StreamEnd must have an empty payload.");
                    transfer.Finished = true;
                    break;
                default: throw new InvalidDataException("Expected file.put.data StreamData or StreamEnd.");
            }
        }
        catch
        {
            failed = true;
            throw;
        }
        finally { transfer.Lock.Release(); if (failed) await RemoveTransferAsync(transfer.Id).ConfigureAwait(false); }
    }

    private ProtocolMessage Stat(ProtocolMessage request)
    {
        DemandPermission();
        var path = ParsePath(request.Payload, out _);
        EnsureSafeExistingPath(path);
        var info = GetInfo(path);
        return Reply(request, JsonSerializer.SerializeToUtf8Bytes(info, JsonOptions));
    }

    private ProtocolMessage List(ProtocolMessage request)
    {
        DemandPermission();
        var path = ParsePath(request.Payload, out var root);
        var offset = 0;
        if (root.TryGetProperty("offset", out var offsetEl) && (!offsetEl.TryGetInt32(out offset) || offset < 0))
            throw new InvalidDataException("file.list offset must be a nonnegative integer.");
        EnsureSafeExistingPath(path);
        if (!Directory.Exists(path)) throw new IOException("The requested path is not a directory.");
        var entries = Directory.EnumerateFileSystemEntries(path)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Skip(offset).Take(MaxListEntries + 1).ToArray();
        var hasMore = entries.Length > MaxListEntries;
        var result = new ListResponse(entries.Take(MaxListEntries).Select(entry =>
        {
            EnsureSafeExistingPath(entry);
            return GetInfo(entry, Path.GetFileName(entry));
        }).ToArray(), hasMore);
        var json = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
        if (json.Length > MaxJsonBytes) throw new InvalidDataException("Directory page exceeds the response size limit.");
        return Reply(request, json);
    }

    private ProtocolMessage Mkdir(ProtocolMessage request)
    {
        DemandPermission();
        var path = ParsePath(request.Payload, out _);
        CreateDirectoriesSafely(path);
        if (!Directory.Exists(path)) throw new IOException("The target exists and is not a directory.");
        return Reply(request, []);
    }

    private ValueTask<ProtocolMessage> OpenUploadAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        DemandPermission();
        cancellationToken.ThrowIfCancellationRequested();
        using var doc = ParseJson(request.Payload);
        var root = RequireObject(doc.RootElement);
        var path = ResolvePath(RequiredString(root, "path"));
        var overwrite = OptionalBoolean(root, "overwrite", false);
        long? timestamp = OptionalInt64(root, "lastWriteUnixMs");
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("File path has no parent directory.");
        EnsureSafeExistingPath(parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The destination directory does not exist; use file.mkdir first.");
        EnsureSafeDestination(path);
        if (!overwrite && File.Exists(path)) throw new IOException("The destination already exists.");
        ReserveTransferSlot();
        string tempPath = Path.Combine(parent, ".xas-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var stream = new FileStream(tempPath, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.SequentialScan });
            var transfer = AddTransfer(TransferKind.Upload, path, tempPath, overwrite, timestamp, stream);
            return ValueTask.FromResult(Reply(request, UInt32Bytes(transfer.Id)));
        }
        catch { ReleaseOpening(); TryDelete(tempPath); throw; }
    }

    private async ValueTask<ProtocolMessage> CommitUploadAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        DemandPermission();
        cancellationToken.ThrowIfCancellationRequested();
        var id = ReadId(request.Payload, "file.put.commit");
        var transfer = GetTransfer(id, TransferKind.Upload);
        await transfer.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!transfer.Finished) throw new InvalidOperationException("Upload stream has not ended.");
            await transfer.Stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
            transfer.Stream.Flush(true);
            await transfer.Stream.DisposeAsync().ConfigureAwait(false);
            transfer.Stream = null;
            EnsureSafeExistingPath(Path.GetDirectoryName(transfer.Path)!);
            EnsureSafeDestination(transfer.Path);
            if (transfer.Timestamp is long timestamp)
            {
                try { File.SetLastWriteTimeUtc(transfer.TempPath, DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime); }
                catch (ArgumentOutOfRangeException) { }
            }
            File.Move(transfer.TempPath, transfer.Path, transfer.Overwrite);
            await RemoveTransferAsync(id, alreadyLocked: true).ConfigureAwait(false);
            return Reply(request, Int64Bytes(transfer.Bytes));
        }
        catch
        {
            await RemoveTransferAsync(id, alreadyLocked: true).ConfigureAwait(false);
            throw;
        }
        finally { transfer.Lock.Release(); }
    }

    private async ValueTask<ProtocolMessage> OpenDownloadAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        DemandPermission();
        cancellationToken.ThrowIfCancellationRequested();
        using var doc = ParseJson(request.Payload);
        var path = ResolvePath(RequiredString(RequireObject(doc.RootElement), "path"));
        EnsureSafeExistingPath(path);
        if (!File.Exists(path)) throw new IOException("The requested path is not a file.");
        ReserveTransferSlot();
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, MaxFrameBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
            var transfer = AddTransfer(TransferKind.Download, path, "", false, null, stream);
            stream = null;
            _ = PumpDownloadAsync(transfer);
            return Reply(request, DownloadOpenBytes(transfer.Id, length, lastWrite));
        }
        catch { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); ReleaseOpening(); throw; }
    }

    private async Task PumpDownloadAsync(Transfer transfer)
    {
        var buffer = new byte[MaxFrameBytes];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, transfer.Cancel.Token);
        try
        {
            while (true)
            {
                var count = await transfer.Stream!.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (count == 0) break;
                var payload = count == buffer.Length ? buffer : buffer.AsSpan(0, count).ToArray();
                await _send(new ProtocolMessage(MessageKind.StreamData, 0, transfer.Id, "file.get.data", payload), linked.Token).ConfigureAwait(false);
            }
            await _send(new ProtocolMessage(MessageKind.StreamEnd, 0, transfer.Id, "file.get.data", []), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (linked.IsCancellationRequested) { }
        finally { await RemoveTransferAsync(transfer.Id).ConfigureAwait(false); }
    }

    private async ValueTask<ProtocolMessage> CloseDownloadAsync(ProtocolMessage request, CancellationToken cancellationToken)
    {
        DemandPermission();
        cancellationToken.ThrowIfCancellationRequested();
        var id = ReadId(request.Payload, "file.get.close");
        var transfer = GetTransfer(id, TransferKind.Download);
        await RemoveTransferAsync(id).ConfigureAwait(false);
        return Reply(request, []);
    }

    private void DemandPermission()
    {
        if (!_permissions.IsAllowed(_peerId, Capability.FileSystem))
            throw new UnauthorizedAccessException("File system access is not granted on this device for this peer.");
    }

    private void ReserveTransferSlot()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_transfers.Count + _opening >= MaxTransfers) throw new InvalidOperationException("The connection has reached its file transfer limit.");
            if (_nextId > uint.MaxValue) throw new InvalidOperationException("No file transfer IDs remain for this connection.");
            _opening++;
        }
    }

    private Transfer AddTransfer(TransferKind kind, string path, string tempPath, bool overwrite, long? timestamp, FileStream stream)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FileTransferServer));
            _opening--;
            var transfer = new Transfer((uint)_nextId++, kind, path, tempPath, overwrite, timestamp, stream);
            _transfers.Add(transfer.Id, transfer);
            return transfer;
        }
    }
    private void ReleaseOpening() { lock (_gate) _opening--; }

    private Transfer GetTransfer(uint id, TransferKind kind)
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (id != 0 && _transfers.TryGetValue(id, out var transfer) && transfer.Kind == kind) return transfer;
            throw new KeyNotFoundException($"Unknown file transfer ID {id}.");
        }
    }

    private async ValueTask RemoveTransferAsync(uint id, bool alreadyLocked = false)
    {
        Transfer? transfer;
        lock (_gate) { _transfers.Remove(id, out transfer); }
        if (transfer is null) return;
        transfer.Cancel.Cancel();
        if (!alreadyLocked) await transfer.Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (transfer.Stream is not null) await transfer.Stream.DisposeAsync().ConfigureAwait(false);
            if (transfer.Kind == TransferKind.Upload) TryDelete(transfer.TempPath);
        }
        finally
        {
            if (!alreadyLocked) transfer.Lock.Release();
        }
    }

    private static string ResolvePath(string raw)
    {
        if (raw.Length is 0 or > MaxPathChars) throw new InvalidDataException("Path must contain between 1 and 4096 characters.");
        if (raw == "~") raw = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (raw.StartsWith("~/", StringComparison.Ordinal) || raw.StartsWith("~\\", StringComparison.Ordinal))
            raw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), raw[2..]);
        if (raw.Length == 0 || !Path.IsPathFullyQualified(Path.GetFullPath(raw))) raw = Path.GetFullPath(raw);
        var full = Path.GetFullPath(raw);
        if (full.Length > MaxPathChars) throw new InvalidDataException("Resolved path exceeds 4096 characters.");
        return full;
    }

    private static void EnsureSafeExistingPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new IOException("Invalid path root.");
        var current = root;
        RejectReparse(current);
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            RejectReparse(current);
        }
    }

    private static void EnsureSafeDestination(string path)
    {
        EnsureSafeExistingPath(Path.GetDirectoryName(path)!);
        if (File.Exists(path) || Directory.Exists(path)) RejectReparse(path);
        if (Directory.Exists(path)) throw new IOException("Destination is a directory.");
    }

    private static void CreateDirectoriesSafely(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new IOException("Invalid path root.");
        var current = root;
        RejectReparse(current);
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current)) RejectReparse(current);
            else if (File.Exists(current)) throw new IOException("A path component is a file.");
            else Directory.CreateDirectory(current);
        }
    }

    private static void RejectReparse(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links and reparse points are not allowed."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static FileEntry GetInfo(string path, string? name = null)
    {
        var attrs = File.GetAttributes(path);
        bool dir = (attrs & FileAttributes.Directory) != 0;
        return new FileEntry(name, dir ? "directory" : "file", dir ? 0 : new FileInfo(path).Length,
            new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds());
    }

    private static string ParsePath(byte[] bytes, out JsonElement root)
    {
        using var doc = ParseJson(bytes);
        root = doc.RootElement.Clone();
        return ResolvePath(RequiredString(root, "path"));
    }
    private static JsonDocument ParseJson(byte[] bytes)
    {
        if (bytes.Length > 64 * 1024) throw new InvalidDataException("Request JSON exceeds the size limit.");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    }
    private static JsonElement RequireObject(JsonElement element) => element.ValueKind == JsonValueKind.Object ? element : throw new InvalidDataException("Request must be a JSON object.");
    private static string RequiredString(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } s
        ? s : throw new InvalidDataException($"Request requires string field '{key}'.");
    private static bool OptionalBoolean(JsonElement root, string key, bool fallback) => !root.TryGetProperty(key, out var value) ? fallback : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new InvalidDataException($"Field '{key}' must be boolean.");
    private static long? OptionalInt64(JsonElement root, string key) => !root.TryGetProperty(key, out var value) ? null : value.TryGetInt64(out var number) ? number : throw new InvalidDataException($"Field '{key}' must be an integer.");
    private static uint ReadId(byte[] bytes, string method) { if (bytes.Length != 4) throw new InvalidDataException($"{method} payload must be 4 bytes."); var id = BinaryPrimitives.ReadUInt32BigEndian(bytes); return id != 0 ? id : throw new InvalidDataException("Transfer ID must be nonzero."); }
    private static byte[] UInt32Bytes(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
    private static byte[] Int64Bytes(long value) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, value); return b; }
    private static byte[] DownloadOpenBytes(uint id, long length, long timestamp) { var b = new byte[20]; BinaryPrimitives.WriteUInt32BigEndian(b, id); BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(4), length); BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(12), timestamp); return b; }
    private static ProtocolMessage Reply(ProtocolMessage request, byte[] payload) => new(MessageKind.Response, request.RequestId, request.StreamId, request.Method, payload);
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private void ThrowIfDisposed() { lock (_gate) ThrowIfDisposedLocked(); }
    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        Transfer[] transfers;
        lock (_gate) { if (_disposed) return; _disposed = true; transfers = _transfers.Values.ToArray(); _transfers.Clear(); }
        _shutdown.Cancel();
        foreach (var transfer in transfers)
        {
            transfer.Cancel.Cancel();
            await transfer.Lock.WaitAsync().ConfigureAwait(false);
            try { if (transfer.Stream is not null) await transfer.Stream.DisposeAsync().ConfigureAwait(false); if (transfer.Kind == TransferKind.Upload) TryDelete(transfer.TempPath); }
            finally { transfer.Lock.Release(); }
        }
        _shutdown.Dispose();
    }

    private enum TransferKind { Upload, Download }
    private sealed class Transfer(uint id, TransferKind kind, string path, string tempPath, bool overwrite, long? timestamp, FileStream stream)
    {
        internal uint Id { get; } = id; internal TransferKind Kind { get; } = kind; internal string Path { get; } = path;
        internal string TempPath { get; } = tempPath; internal bool Overwrite { get; } = overwrite; internal long? Timestamp { get; } = timestamp;
        internal FileStream? Stream { get; set; } = stream; internal SemaphoreSlim Lock { get; } = new(1, 1);
        internal CancellationTokenSource Cancel { get; } = new(); internal bool Finished { get; set; } internal long Bytes { get; set; }
    }
    private sealed record FileEntry(string? Name, string Kind, long Length, long LastWriteUnixMs);
    private sealed record ListResponse(FileEntry[] Entries, bool HasMore);
}
