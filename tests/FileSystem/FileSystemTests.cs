using Xas.Core.FileSystem;
using Xas.Core;
using Xas.Core.Protocol;
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

        try
        {
            RemoteFileSystemWire.Decode<RemotePath>(new byte[1024 * 1024 + 1]);
            throw new Exception("Oversized filesystem control payload was accepted.");
        }
        catch (InvalidDataException) { }
        await RemoteClientUsesVolumeScopedRequests();
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
                "fs.create" or "fs.delete" or "fs.rename" => [],
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

        var statRequest = RemoteFileSystemWire.Decode<RemotePath>(calls.Single(c => c.Method == "fs.stat").Payload);
        Assert(statRequest.VolumeId == "vol-test" && statRequest.Path == "folder/file.bin",
            "Filesystem client did not scope/normalize the stat request correctly.");
        var rename = RemoteFileSystemWire.Decode<RemoteRenamePath>(calls.Single(c => c.Method == "fs.rename").Payload);
        Assert(rename.Path == "folder/file.bin" && rename.NewPath == "folder/renamed.bin",
            "Filesystem client did not normalize rename paths.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
