using System.Text.Json;

namespace Xas.Core.FileSystem;

/// <summary>Paths are relative to an advertised volume. Volume IDs are opaque and may disappear on eject.</summary>
public sealed record RemoteVolume(string Id, string Name, string Kind, bool ReadOnly);
public sealed record RemoteFileEntry(string Name, bool Directory, long Length, long LastWriteUnixMs);
public sealed record RemoteDirectoryPage(RemoteFileEntry[] Entries, bool HasMore);
public sealed record RemotePath(string VolumeId, string Path);
public sealed record RemoteListPath(string VolumeId, string Path, int Offset);
public sealed record RemoteReadRange(string VolumeId, string Path, long Offset, int Length);
public sealed record RemoteWriteRange(string VolumeId, string Path, long Offset, byte[] Data);
public sealed record RemoteCreatePath(string VolumeId, string Path, bool Directory, bool Replace);
public sealed record RemoteRenamePath(string VolumeId, string Path, string NewPath, bool Replace);
public sealed record RemoteDeletePath(string VolumeId, string Path, bool Directory);
public sealed record RemoteFileStat(string Name, bool Directory, long Length, long LastWriteUnixMs, bool ReadOnly);
public sealed record RemoteWriteResult(long BytesWritten);

public static class RemoteFileSystemWire
{
    public const int MaxChunkBytes = 64 * 1024;
    public const int MaxPathChars = 4096;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    public static T Decode<T>(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 1024 * 1024) throw new InvalidDataException("Filesystem payload exceeds 1 MiB.");
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("Filesystem payload is empty.");
    }
}
