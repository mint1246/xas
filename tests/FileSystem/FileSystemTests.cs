using Xas.Core.FileSystem;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Daemon.FileSystem;
using Xas.Daemon.FileSystem.Mount;

public static class FileSystemTests
{
    public static async Task RunAsync()
    {
        var original = new RemoteWriteRange("volume-1", "folder/file.bin", 12, [0, 1, 128, 255]);
        var encoded = RemoteFileSystemWire.Encode(original);
        var decoded = RemoteFileSystemWire.Decode<RemoteWriteRange>(encoded);
        Assert(decoded.VolumeId == original.VolumeId && decoded.Path == original.Path && decoded.Offset == original.Offset &&
            decoded.Data.SequenceEqual(original.Data), "Filesystem range request did not round-trip.");

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
                "fs.create" or "fs.delete" or "fs.rename" or "fs.truncate" or "fs.setinfo" => [],
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
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
