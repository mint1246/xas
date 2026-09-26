using System.Text.Json;
using Xas.Core;
using Xas.Core.LocalIpc;
using Xas.Core.Protocol;

namespace Xas.Cli;

/// <summary>A local IPC protocol peer pinned to one paired device.</summary>
internal sealed class LocalIpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    public MultiplexedProtocolPeer Peer { get; }

    private LocalIpcConnection(Stream stream)
    {
        _stream = stream;
        Peer = new MultiplexedProtocolPeer(new BinaryFrameConnection(stream, leaveOpen: true), (_, _) =>
            ValueTask.FromException<ProtocolMessage>(new NotSupportedException("The local CLI does not accept requests.")));
    }

    public static async Task<LocalIpcConnection> ConnectAsync(string? deviceId, CancellationToken token)
    {
        var connection = new LocalIpcConnection(await LocalIpcEndpoint.ConnectAsync(token).ConfigureAwait(false));
        try
        {
            _ = await connection.Peer.RequestAsync(LocalIpcProtocol.Bind,
                JsonSerializer.SerializeToUtf8Bytes(new LocalTarget(deviceId), LocalIpcProtocol.Json),
                cancellationToken: token).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await Peer.DisposeAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
