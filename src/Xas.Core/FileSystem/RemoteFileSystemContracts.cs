using System.Text.Json;
using System.Buffers.Binary;
using System.Text;

namespace Xas.Core.FileSystem;

/// <summary>Paths are relative to an advertised volume. Volume IDs are opaque and may disappear on eject.</summary>
public sealed record RemoteVolume(string Id, string Name, string Kind, bool ReadOnly,
    long? TotalBytes = null, long? FreeBytes = null, string? FileSystem = null);
public sealed record RemoteFileEntry(string Name, bool Directory, long Length, long LastWriteUnixMs);
public sealed record RemoteDirectoryPage(RemoteFileEntry[] Entries, bool HasMore);
public sealed record RemotePath(string VolumeId, string Path);
public sealed record RemoteVolumeRequest(string VolumeId);
public sealed record RemoteListPath(string VolumeId, string Path, int Offset);
public sealed record RemoteReadRange(string VolumeId, string Path, long Offset, int Length);
public sealed record RemoteWriteRange(string VolumeId, string Path, long Offset, byte[] Data);
public sealed record RemoteBinaryWriteRange(string VolumeId, string Path, long Offset, ReadOnlyMemory<byte> Data);
public sealed record RemoteCreatePath(string VolumeId, string Path, bool Directory, bool Replace);
public sealed record RemoteRenamePath(string VolumeId, string Path, string NewPath, bool Replace);
public sealed record RemoteDeletePath(string VolumeId, string Path, bool Directory);
public sealed record RemoteSetLength(string VolumeId, string Path, long Length);
public sealed record RemoteSetInfo(string VolumeId, string Path, long? CreationUnixMs,
    long? LastAccessUnixMs, long? LastWriteUnixMs, bool? ReadOnly);
public sealed record RemoteFlushPath(string VolumeId, string Path, bool Directory);
public sealed record RemoteFileStat(string Name, bool Directory, long Length, long LastWriteUnixMs, bool ReadOnly);
public sealed record RemoteWriteResult(long BytesWritten);

public static class RemoteFileSystemWire
{
    public const int LegacyMaxChunkBytes = 64 * 1024;
    public const int MaxChunkBytes = 768 * 1024;
    public const int MaxPathChars = 4096;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    public static T Decode<T>(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 1024 * 1024) throw new InvalidDataException("Filesystem payload exceeds 1 MiB.");
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("Filesystem payload is empty.");
    }

    public static byte[] EncodeWriteV2(string volumeId, string path, long offset, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        ArgumentNullException.ThrowIfNull(path);
        if (data.Length > MaxChunkBytes) throw new ArgumentOutOfRangeException(nameof(data));
        var volume = StrictUtf8.GetBytes(volumeId);
        var remotePath = StrictUtf8.GetBytes(path);
        if (volume.Length > ushort.MaxValue || remotePath.Length > ushort.MaxValue)
            throw new ArgumentException("Filesystem identifier/path is too long for the binary write format.");
        const int headerBytes = 16;
        var payload = new byte[checked(headerBytes + volume.Length + remotePath.Length + data.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), checked((ushort)volume.Length));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), checked((ushort)remotePath.Length));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(4, 8), offset);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12, 4), data.Length);
        volume.CopyTo(payload.AsSpan(headerBytes));
        remotePath.CopyTo(payload.AsSpan(headerBytes + volume.Length));
        data.CopyTo(payload.AsSpan(headerBytes + volume.Length + remotePath.Length));
        return payload;
    }

    public static RemoteBinaryWriteRange DecodeWriteV2(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        const int headerBytes = 16;
        if (payload.Length < headerBytes) throw new InvalidDataException("Binary filesystem write payload is truncated.");
        var volumeLength = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2));
        var pathLength = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2, 2));
        var offset = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(4, 8));
        var dataLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(12, 4));
        if (dataLength is < 0 or > MaxChunkBytes) throw new InvalidDataException("Binary filesystem write length is invalid.");
        var dataOffset = checked(headerBytes + volumeLength + pathLength);
        if (dataOffset > payload.Length || payload.Length - dataOffset != dataLength)
            throw new InvalidDataException("Binary filesystem write payload lengths are invalid.");
        try
        {
            var volumeId = StrictUtf8.GetString(payload, headerBytes, volumeLength);
            var path = StrictUtf8.GetString(payload, headerBytes + volumeLength, pathLength);
            if (string.IsNullOrWhiteSpace(volumeId)) throw new InvalidDataException("Binary filesystem write volume id is empty.");
            return new RemoteBinaryWriteRange(volumeId, path, offset, payload.AsMemory(dataOffset, dataLength));
        }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Binary filesystem write text is not valid UTF-8.", ex); }
    }
}
