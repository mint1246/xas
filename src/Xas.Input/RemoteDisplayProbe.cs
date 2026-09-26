using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Transport;

namespace Xas.Input;

/// <summary>
/// Fetches the paired Linux display's mode over the authenticated channel, so the Windows side can size a
/// virtual monitor to match instead of relying on a fixed mode list.
/// </summary>
public static class RemoteDisplayProbe
{
    public static async Task<DisplayMetadata?> TryFetchAsync(ConfiguredPeer configured, DeviceIdentity identity,
        PeerTrustStore trust, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configured);
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setup.CancelAfter(TimeSpan.FromSeconds(5));
        await using var connection = await MutualTlsTransport.ConnectAsync(configured.Host, configured.Port,
            identity, trust, configured.DeviceId, TimeSpan.FromSeconds(3), setup.Token).ConfigureAwait(false);
        await using var frames = new BinaryFrameConnection(connection.Stream, leaveOpen: true);
        await using var peer = new MultiplexedProtocolPeer(frames, (_, _) =>
            ValueTask.FromException<ProtocolMessage>(
                new NotSupportedException("The display probe does not accept remote requests.")));

        var infoReply = await peer.RequestAsync("device.info", [], cancellationToken: setup.Token)
            .ConfigureAwait(false);
        var info = JsonSerializer.Deserialize<DeviceInfo>(infoReply.Payload)
            ?? throw new InvalidDataException("Invalid device.info response.");
        if (info.DeviceId != configured.DeviceId)
            throw new InvalidDataException("Device identity changed during display setup.");
        if (!info.Capabilities.Any(c => c.Capability == Capability.Input && c.Version >= 2) ||
            !info.Capabilities.Any(c => c.Capability == Capability.Display && c.Version >= 1))
            return null;

        var displayReply = await peer.RequestAsync("display.info", [], cancellationToken: setup.Token)
            .ConfigureAwait(false);
        var display = JsonSerializer.Deserialize<DisplayMetadata>(displayReply.Payload)
            ?? throw new InvalidDataException("The remote display metadata was empty.");
        if (display.WidthPixels is < 1 or > 16384 || display.HeightPixels is < 1 or > 16384) return null;
        return display;
    }
}
