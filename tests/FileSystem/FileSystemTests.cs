using Xas.Core.FileSystem;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Daemon.FileSystem;
using Xas.Daemon.FileSystem.Mount;
using System.Threading.Channels;

public static class FileSystemTests
{
    public static async Task RunAsync()
    {
        var original = new RemoteWriteRange("volume-1", "folder/file.bin", 12, [0, 1, 128, 255]);
        var encoded = RemoteFileSystemWire.Encode(original);
        var decoded = RemoteFileSystemWire.Decode<RemoteWriteRange>(encoded);
        Assert(decoded.VolumeId == original.VolumeId && decoded.Path == original.Path && decoded.Offset == original.Offset &&
            decoded.Data.SequenceEqual(original.Data), "Filesystem range request did not round-trip.");

        var binaryData = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        var binary = RemoteFileSystemWire.EncodeWriteV2("volume-2", "תיקיה/file.bin", 123456789, binaryData);
        var decodedBinary = RemoteFileSystemWire.DecodeWriteV2(binary);
        Assert(decodedBinary.VolumeId == "volume-2" && decodedBinary.Path == "תיקיה/file.bin" &&
               decodedBinary.Offset == 123456789 && decodedBinary.Data.Span.SequenceEqual(binaryData),
            "Binary filesystem write request did not round-trip.");

        var page = new RemoteDirectoryPage([new RemoteFileEntry("item", false, 5, 10)], true);
        var decodedPage = RemoteFileSystemWire.Decode<RemoteDirectoryPage>(RemoteFileSystemWire.Encode(page));
        Assert(decodedPage.HasMore && decodedPage.Entries.Single().Length == 5, "Filesystem directory page did not round-trip.");
        var volume = new RemoteVolume("vol", "SD", "removable", false, 1_000_000, 250_000, "exfat");
        var decodedVolume = RemoteFileSystemWire.Decode<RemoteVolume>(RemoteFileSystemWire.Encode(volume));
        Assert(decodedVolume.TotalBytes == 1_000_000 && decodedVolume.FreeBytes == 250_000 && decodedVolume.FileSystem == "exfat",
            "Filesystem volume capacity metadata did not round-trip.");

        try
        {
            RemoteFileSystemWire.Decode<RemotePath>(new byte[1024 * 1024 + 1]);
            throw new Exception("Oversized filesystem control payload was accepted.");
        }
        catch (InvalidDataException) { }
        await RemoteClientUsesVolumeScopedRequests();
        await RemoteClientUsesBinaryWritesForVersion2();
        await RemoteErrorsRetainFilesystemSemantics();
        await FileSystemExportsAreExplicitAndReadOnlyIsEnforced();
    }

    private static async Task FileSystemExportsAreExplicitAndReadOnlyIsEnforced()
    {
        var root = Path.Combine(Path.GetTempPath(), "xas-fs-policy-" + Guid.NewGuid().ToString("N"));
        var exportRoot = Path.Combine(root, "export");
        Directory.CreateDirectory(exportRoot);
        await File.WriteAllTextAsync(Path.Combine(exportRoot, "hello.txt"), "hello");
        try
        {
            var configuration = new LocalConfiguration(Path.Combine(root, "config"));
            configuration.SetAutoExposeRemovable(false);
            configuration.UpsertFileSystemExport(new FileSystemExport("docs", exportRoot, "Docs", ReadOnly: true));
            var permissions = new PeerPermissionStore(Path.Combine(root, "trust"));
            permissions.SetAllowed("peer", Capability.FileSystem, true);
            var service = new FileSystemService(permissions, configuration);

            var volumesReply = await service.HandleAsync("peer",
                new ProtocolMessage(MessageKind.Request, 1, 0, "fs.volumes", []), CancellationToken.None);
            var volumes = RemoteFileSystemWire.Decode<RemoteVolume[]>(volumesReply.Payload);
            Assert(volumes.Length == 1 && volumes[0] is { Id: "export-docs", Name: "Docs", Kind: "export", ReadOnly: true },
                "Filesystem service exposed storage outside the explicit export policy.");

            var readReply = await service.HandleAsync("peer", new ProtocolMessage(MessageKind.Request, 2, 0, "fs.read",
                RemoteFileSystemWire.Encode(new RemoteReadRange("export-docs", "hello.txt", 0, 5))), CancellationToken.None);
            Assert(System.Text.Encoding.UTF8.GetString(readReply.Payload) == "hello", "Explicit filesystem export could not be read.");
            var flushReply = await service.HandleAsync("peer", new ProtocolMessage(MessageKind.Request, 4, 0, "fs.flush",
                RemoteFileSystemWire.Encode(new RemoteFlushPath("export-docs", "hello.txt", false))), CancellationToken.None);
            Assert(flushReply.Payload.Length == 0, "Filesystem service returned an invalid durable flush response.");
            if (OperatingSystem.IsLinux())
            {
                var directoryFlush = await service.HandleAsync("peer", new ProtocolMessage(MessageKind.Request, 5, 0, "fs.flush",
                    RemoteFileSystemWire.Encode(new RemoteFlushPath("export-docs", "", true))), CancellationToken.None);
                Assert(directoryFlush.Payload.Length == 0, "Filesystem service returned an invalid directory flush response.");
            }

            try
            {
                await service.HandleAsync("peer", new ProtocolMessage(MessageKind.Request, 3, 0, "fs.write",
                    RemoteFileSystemWire.Encode(new RemoteWriteRange("export-docs", "hello.txt", 0, [1, 2, 3]))), CancellationToken.None);
                throw new Exception("A read-only filesystem export accepted a write.");
            }
            catch (UnauthorizedAccessException) { }

            var reloaded = new LocalConfiguration(Path.Combine(root, "config"));
            Assert(!reloaded.AutoExposeRemovable && reloaded.FileSystemExports.Single().Id == "docs",
                "Filesystem export policy did not persist in local configuration.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task RemoteClientUsesVolumeScopedRequests()
    {
        var calls = new List<(string Method, byte[] Payload)>();
        var client = new RemoteFileSystemOperationsClient("vol-test", (method, payload, _) =>
        {
            calls.Add((method, payload));
            byte[] response = method switch
            {
                "fs.stat" => RemoteFileSystemWire.Encode(new RemoteFileStat("file.bin", false, 10, 20, false)),
                "fs.list" => RemoteFileSystemWire.Encode(new RemoteDirectoryPage([], false)),
                "fs.read" => [1, 2, 3],
                "fs.write" => RemoteFileSystemWire.Encode(new RemoteWriteResult(3)),
                "fs.create" or "fs.delete" or "fs.rename" or "fs.truncate" or "fs.setinfo" or "fs.flush" => [],
                _ => throw new Exception("Unexpected filesystem method: " + method)
            };
            return ValueTask.FromResult(new ProtocolMessage(MessageKind.Response, 1, 0, method, response));
        });

        var stat = await client.StatAsync("/folder/file.bin", CancellationToken.None);
        Assert(stat.Length == 10, "Remote stat result was not decoded.");
        _ = await client.ListAsync("/folder", 0, CancellationToken.None);
        Assert((await client.ReadAsync("/folder/file.bin", 2, 3, CancellationToken.None)).SequenceEqual(new byte[] { 1, 2, 3 }),
            "Remote read result was not returned.");
        Assert(await client.WriteAsync("/folder/file.bin", 4, new byte[] { 7, 8, 9 }, CancellationToken.None) == 3,
            "Remote write count was not decoded.");
        await client.CreateAsync("/folder/new.bin", false, false, CancellationToken.None);
        await client.DeleteAsync("/folder/new.bin", false, CancellationToken.None);
        await client.RenameAsync("/folder/file.bin", "/folder/renamed.bin", false, CancellationToken.None);
        await client.SetLengthAsync("/folder/renamed.bin", 123, CancellationToken.None);
        await client.SetInfoAsync("/folder/renamed.bin", 10, 20, 30, true, CancellationToken.None);
        await client.FlushAsync("/folder/renamed.bin", false, CancellationToken.None);

        var statRequest = RemoteFileSystemWire.Decode<RemotePath>(calls.Single(c => c.Method == "fs.stat").Payload);
        Assert(statRequest.VolumeId == "vol-test" && statRequest.Path == "folder/file.bin",
            "Filesystem client did not scope/normalize the stat request correctly.");
        var rename = RemoteFileSystemWire.Decode<RemoteRenamePath>(calls.Single(c => c.Method == "fs.rename").Payload);
        Assert(rename.Path == "folder/file.bin" && rename.NewPath == "folder/renamed.bin",
            "Filesystem client did not normalize rename paths.");
        var truncate = RemoteFileSystemWire.Decode<RemoteSetLength>(calls.Single(c => c.Method == "fs.truncate").Payload);
        Assert(truncate.VolumeId == "vol-test" && truncate.Path == "folder/renamed.bin" && truncate.Length == 123,
            "Filesystem client did not encode truncation correctly.");
        var setInfo = RemoteFileSystemWire.Decode<RemoteSetInfo>(calls.Single(c => c.Method == "fs.setinfo").Payload);
        Assert(setInfo.Path == "folder/renamed.bin" && setInfo.CreationUnixMs == 10 && setInfo.LastAccessUnixMs == 20 &&
               setInfo.LastWriteUnixMs == 30 && setInfo.ReadOnly == true,
            "Filesystem client did not encode basic metadata updates correctly.");
        var flush = RemoteFileSystemWire.Decode<RemoteFlushPath>(calls.Single(c => c.Method == "fs.flush").Payload);
        Assert(flush.VolumeId == "vol-test" && flush.Path == "folder/renamed.bin" && !flush.Directory,
            "Filesystem client did not encode the durable flush request correctly.");
    }

    private static async Task RemoteErrorsRetainFilesystemSemantics()
    {
        var (left, right) = InMemoryFrameConnection.CreatePair();
        await using var clientPeer = new MultiplexedProtocolPeer(left,
            (_, _) => ValueTask.FromException<ProtocolMessage>(new Exception("Unexpected request.")));
        await using var serverPeer = new MultiplexedProtocolPeer(right, (request, _) =>
            request.Method switch
            {
                "fs.stat" => ValueTask.FromException<ProtocolMessage>(new FileNotFoundException("missing")),
                "fs.list" => ValueTask.FromException<ProtocolMessage>(new UnauthorizedAccessException("access denied")),
                "fs.read" => ValueTask.FromException<ProtocolMessage>(new IOException("Destination exists")),
                _ => ValueTask.FromResult(new ProtocolMessage(MessageKind.Error, 0, 0, request.Method,
                    System.Text.Encoding.UTF8.GetBytes("legacy text")))
            });
        var client = new RemoteFileSystemOperationsClient("vol",
            (method, payload, token) => clientPeer.RequestAsync(method, payload, cancellationToken: token));
        try
        {
            await client.StatAsync("missing", CancellationToken.None);
            throw new Exception("Remote missing-file response unexpectedly succeeded.");
        }
        catch (FileNotFoundException ex)
        { Assert(ex.Message.Contains("missing", StringComparison.Ordinal), "Remote file error lost its message."); }

        try
        {
            await client.ListAsync("folder", 0, CancellationToken.None);
            throw new Exception("Remote access-denied response unexpectedly succeeded.");
        }
        catch (UnauthorizedAccessException ex)
        { Assert(ex.Message.Contains("access denied", StringComparison.Ordinal), "Remote access-denied error lost its message."); }

        try
        {
            await client.ReadAsync("file", 0, 1, CancellationToken.None);
            throw new Exception("Remote I/O conflict unexpectedly succeeded.");
        }
        catch (IOException ex)
        { Assert(ex.Message.Contains("exists", StringComparison.Ordinal), "Remote I/O conflict lost its message."); }

        var legacy = new RemoteFileSystemOperationsClient("vol",
            (method, payload, token) => clientPeer.RequestAsync("old." + method, payload, cancellationToken: token));
        try
        {
            await legacy.StatAsync("missing", CancellationToken.None);
            throw new Exception("Legacy remote error unexpectedly succeeded.");
        }
        catch (RemoteProtocolException) { }
    }

    private sealed class InMemoryFrameConnection : IFrameConnection
    {
        private readonly Channel<ProtocolMessage> _incoming;
        private readonly Channel<ProtocolMessage> _outgoing;
        private InMemoryFrameConnection(Channel<ProtocolMessage> incoming, Channel<ProtocolMessage> outgoing)
        { _incoming = incoming; _outgoing = outgoing; }
        public static (InMemoryFrameConnection Left, InMemoryFrameConnection Right) CreatePair()
        {
            var left = Channel.CreateUnbounded<ProtocolMessage>();
            var right = Channel.CreateUnbounded<ProtocolMessage>();
            return (new InMemoryFrameConnection(left, right), new InMemoryFrameConnection(right, left));
        }
        public ValueTask SendAsync(ProtocolMessage message, CancellationToken cancellationToken) =>
            _outgoing.Writer.WriteAsync(message, cancellationToken);
        public async ValueTask<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken) =>
            await _incoming.Reader.ReadAsync(cancellationToken);
        public ValueTask DisposeAsync()
        {
            _outgoing.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task RemoteClientUsesBinaryWritesForVersion2()
    {
        var calls = new List<(string Method, byte[] Payload)>();
        var client = new RemoteFileSystemOperationsClient("vol-v2", (method, payload, _) =>
        {
            calls.Add((method, payload));
            return ValueTask.FromResult(new ProtocolMessage(MessageKind.Response, 1, 0, method,
                RemoteFileSystemWire.Encode(new RemoteWriteResult(100_000))));
        }, protocolVersion: 2);
        Assert(client.MaxTransferBytes == RemoteFileSystemWire.MaxChunkBytes,
            "Filesystem v2 client did not advertise the larger transfer limit.");
        var data = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 253)).ToArray();
        Assert(await client.WriteAsync("/folder/large.bin", 77, data, CancellationToken.None) == data.Length,
            "Filesystem v2 client returned the wrong write count.");
        var call = calls.Single();
        Assert(call.Method == "fs.write.v2", "Filesystem v2 client did not select the binary write method.");
        var decoded = RemoteFileSystemWire.DecodeWriteV2(call.Payload);
        Assert(decoded.VolumeId == "vol-v2" && decoded.Path == "folder/large.bin" && decoded.Offset == 77 &&
               decoded.Data.Span.SequenceEqual(data), "Filesystem v2 client encoded the wrong binary write payload.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
