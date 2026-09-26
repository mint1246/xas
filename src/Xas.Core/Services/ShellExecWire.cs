using System.Buffers.Binary;

namespace Xas.Core.Services;

/// <summary>Framing for a one-shot command whose standard streams remain live.</summary>
public static class ShellExecWire
{
    public const int MaxChunkBytes = 64 * 1024;
    public const string Open = "shell.exec.open";
    public const string Stdin = "shell.exec.stdin";
    public const string Stdout = "shell.exec.stdout";
    public const string Stderr = "shell.exec.stderr";
    public const string Exit = "shell.exec.exit";
    public const string Error = "shell.exec.error";
    public const string Close = "shell.exec.close";

    public static byte[] EncodeSessionId(uint sessionId)
    {
        if (sessionId == 0) throw new ArgumentOutOfRangeException(nameof(sessionId));
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, sessionId);
        return payload;
    }

    public static uint DecodeSessionId(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4) throw new InvalidDataException("Shell session ID must be four bytes.");
        var sessionId = BinaryPrimitives.ReadUInt32BigEndian(payload);
        if (sessionId == 0) throw new InvalidDataException("Shell session ID cannot be zero.");
        return sessionId;
    }

    public static byte[] EncodeExitCode(int exitCode)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, exitCode);
        return payload;
    }

    public static int DecodeExitCode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4) throw new InvalidDataException("Shell exit code must be four bytes.");
        return BinaryPrimitives.ReadInt32BigEndian(payload);
    }
}
