using System.Buffers.Binary;
using System.Text.Json;

namespace Xas.Core.Services;

public sealed record ShellResult(int ExitCode, byte[] StandardOutput, byte[] StandardError, bool OutputTruncated);

public static class ShellWire
{
    private const int HeaderLength = 13;

    public static byte[] EncodeRequest(ShellRequest request) => JsonSerializer.SerializeToUtf8Bytes(request);

    public static ShellRequest DecodeRequest(byte[] payload)
    {
        if (payload.Length > 64 * 1024) throw new InvalidDataException("Shell request is too large.");
        return JsonSerializer.Deserialize<ShellRequest>(payload) ?? throw new InvalidDataException("Invalid shell request.");
    }

    public static byte[] EncodeResult(ShellResult result)
    {
        var length = checked(HeaderLength + result.StandardOutput.Length + result.StandardError.Length);
        if (length + 32 > XasProtocol.MaxFrameBytes) throw new ArgumentException("Shell output exceeds one frame.");
        var bytes = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), result.ExitCode);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4, 4), result.StandardOutput.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), result.StandardError.Length);
        bytes[12] = result.OutputTruncated ? (byte)1 : (byte)0;
        result.StandardOutput.CopyTo(bytes.AsSpan(HeaderLength));
        result.StandardError.CopyTo(bytes.AsSpan(HeaderLength + result.StandardOutput.Length));
        return bytes;
    }

    public static ShellResult DecodeResult(byte[] payload)
    {
        if (payload.Length < HeaderLength) throw new InvalidDataException("Shell result is truncated.");
        var stdoutLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4, 4));
        var stderrLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(8, 4));
        if (stdoutLength < 0 || stderrLength < 0 || HeaderLength + (long)stdoutLength + stderrLength != payload.Length)
            throw new InvalidDataException("Invalid shell result lengths.");
        if (payload[12] > 1) throw new InvalidDataException("Invalid shell result flags.");
        return new ShellResult(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4)),
            payload.AsSpan(HeaderLength, stdoutLength).ToArray(),
            payload.AsSpan(HeaderLength + stdoutLength, stderrLength).ToArray(), payload[12] == 1);
    }
}
