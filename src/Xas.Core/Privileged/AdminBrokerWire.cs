using System.Buffers.Binary;
using System.Text.Json;

namespace Xas.Core.Privileged;

public enum AdminFrameKind : byte { Stdin = 1, Stdout = 2, Stderr = 3, EndStdin = 4, Exit = 5, Error = 6, Resize = 7 }

public sealed record AdminBrokerRequest(ShellMode Mode, string? Command, string? Executable,
    string[] Arguments, string? WorkingDirectory, short Columns = 80, short Rows = 24);

public sealed record AdminBrokerStatus(bool Ok, string? Error = null);

/// <summary>Bounded length-prefixed headers and frames for the local privileged broker.</summary>
public static class AdminBrokerWire
{
    public const int MaxHeader = 64 * 1024;
    public const int MaxFrame = 64 * 1024;
    private static readonly SemaphoreSlim FrameWriteLock = new(1, 1);

    public static async Task WriteHeaderAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaxHeader) throw new InvalidDataException("Admin broker header is too large.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T> ReadHeaderAsync<T>(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is < 1 or > MaxHeader) throw new InvalidDataException("Invalid admin broker header length.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Invalid admin broker header.");
    }

    public static async Task WriteFrameAsync(Stream stream, AdminFrameKind kind, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (data.Length > MaxFrame) throw new InvalidDataException("Admin broker frame is too large.");
        await FrameWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prefix = new byte[5];
            prefix[0] = (byte)kind;
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(1), data.Length);
            await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
            if (!data.IsEmpty) await stream.WriteAsync(data, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { FrameWriteLock.Release(); }
    }

    public static async Task<(AdminFrameKind Kind, byte[] Data)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[5];
        await stream.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(1));
        if (size is < 0 or > MaxFrame) throw new InvalidDataException("Invalid admin broker frame length.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return ((AdminFrameKind)prefix[0], bytes);
    }
}
