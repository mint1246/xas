using Xas.Core.FileSystem;

public static class FileSystemTests
{
    public static Task RunAsync()
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
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
