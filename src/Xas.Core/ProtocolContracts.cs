namespace Xas.Core;

public static class XasProtocol
{
    public const ushort Version = 1;
    public const int MaxFrameBytes = 1024 * 1024;
    public const int DefaultPort = 47821;
}

public enum MessageKind : byte
{
    Hello = 1,
    Request = 2,
    Response = 3,
    Event = 4,
    StreamData = 5,
    StreamEnd = 6,
    Cancel = 7,
    Error = 8
}

/// <summary>Control payloads are UTF-8 JSON; stream payloads are raw bytes.</summary>
public sealed record ProtocolMessage(MessageKind Kind, uint RequestId, uint StreamId,
    string Method, byte[] Payload);

public interface IFrameConnection : IAsyncDisposable
{
    ValueTask SendAsync(ProtocolMessage message, CancellationToken cancellationToken);
    ValueTask<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken);
}
