using System.Buffers.Binary;
using System.Text;

namespace Xas.Core.Protocol;

/// <summary>Reads and writes bounded, length-prefixed XAS protocol messages.</summary>
public sealed class BinaryFrameConnection : IFrameConnection
{
    private const int HeaderBytes = 13; // version, kind, request id, stream id, method byte count
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _receiveLock = new(1, 1);
    private int _disposed;

    public BinaryFrameConnection(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("The stream must support reading and writing.", nameof(stream));
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    public async ValueTask SendAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        if (!Enum.IsDefined(message.Kind)) throw new ArgumentOutOfRangeException(nameof(message), "Unknown message kind.");
        ArgumentNullException.ThrowIfNull(message.Method);
        ArgumentNullException.ThrowIfNull(message.Payload);

        byte[] method;
        try { method = StrictUtf8.GetBytes(message.Method); }
        catch (EncoderFallbackException ex) { throw new ArgumentException("Method is not valid UTF-8 text.", nameof(message), ex); }
        if (method.Length > ushort.MaxValue) throw new ArgumentException("Method is too long.", nameof(message));
        var frameLength = checked(HeaderBytes + method.Length + message.Payload.Length);
        if (frameLength > XasProtocol.MaxFrameBytes) throw new ArgumentException("Frame exceeds the protocol limit.", nameof(message));

        var frame = new byte[4 + frameLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)frameLength);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), XasProtocol.Version);
        frame[6] = (byte)message.Kind;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(7, 4), message.RequestId);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(11, 4), message.StreamId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(15, 2), (ushort)method.Length);
        method.CopyTo(frame.AsSpan(17));
        message.Payload.CopyTo(frame.AsSpan(17 + method.Length));

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    public async ValueTask<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _receiveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var prefix = new byte[4];
            var gotPrefix = await ReadExactlyOrEofAsync(prefix, allowInitialEof: true, cancellationToken).ConfigureAwait(false);
            if (!gotPrefix) return null;
            var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (length < HeaderBytes || length > XasProtocol.MaxFrameBytes)
                throw new InvalidDataException("Invalid frame length.");
            var body = new byte[(int)length];
            await ReadExactlyOrEofAsync(body, allowInitialEof: false, cancellationToken).ConfigureAwait(false);
            var version = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0, 2));
            if (version != XasProtocol.Version) throw new InvalidDataException($"Unsupported protocol version {version}.");
            var kind = (MessageKind)body[2];
            if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown message kind.");
            var requestId = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(3, 4));
            var streamId = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(7, 4));
            var methodLength = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(11, 2));
            if (methodLength > body.Length - HeaderBytes) throw new InvalidDataException("Method length exceeds frame length.");
            string method;
            try { method = StrictUtf8.GetString(body, HeaderBytes, methodLength); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Method is not valid UTF-8.", ex); }
            var payload = body.AsSpan(HeaderBytes + methodLength).ToArray();
            return new ProtocolMessage(kind, requestId, streamId, method, payload);
        }
        finally { _receiveLock.Release(); }
    }

    private async ValueTask<bool> ReadExactlyOrEofAsync(Memory<byte> buffer, bool allowInitialEof, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0 && allowInitialEof) return false;
                throw new EndOfStreamException("Connection ended in the middle of a frame.");
            }
            offset += read;
        }
        return true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_leaveOpen) await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
