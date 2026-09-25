using System.Text;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Transport;

namespace Xas.Cli.Clipboard;

/// <summary>Explicit text clipboard transfer over a pinned mutual TLS connection.</summary>
public static class ClipboardClient
{
    private const int MaxTextBytes = 256 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task PushAsync(string? deviceId, LocalConfiguration config, DeviceIdentity identity,
        PeerTrustStore trust, ITextClipboardBackend localBackend, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(localBackend);
        if (!localBackend.IsAvailable) throw new InvalidOperationException("A user-session text clipboard is unavailable.");

        var peer = Resolve(config, deviceId);
        await using var protocol = await ConnectAsync(peer, identity, trust, token).ConfigureAwait(false);
        await RequireCapabilityAsync(protocol, peer.DeviceId, token).ConfigureAwait(false);

        var local = await localBackend.GetSnapshotAsync(token).ConfigureAwait(false);
        EnsureTextWithinLimit(local.Text);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new ClipboardPayload(identity.DeviceId, local.ChangeId, local.Text), JsonOptions);
        var reply = await protocol.RequestAsync("clipboard.set", payload, cancellationToken: token).ConfigureAwait(false);
        if (reply.Payload.Length != 0) throw new InvalidDataException("Invalid clipboard.set response.");
    }

    public static async Task PullAsync(string? deviceId, LocalConfiguration config, DeviceIdentity identity,
        PeerTrustStore trust, ITextClipboardBackend localBackend, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(localBackend);
        if (!localBackend.IsAvailable) throw new InvalidOperationException("A user-session text clipboard is unavailable.");

        var peer = Resolve(config, deviceId);
        await using var protocol = await ConnectAsync(peer, identity, trust, token).ConfigureAwait(false);
        await RequireCapabilityAsync(protocol, peer.DeviceId, token).ConfigureAwait(false);
        var remote = await GetRemoteClipboardAsync(protocol, token).ConfigureAwait(false);
        if (remote.Origin == identity.DeviceId) return;
        EnsureTextWithinLimit(remote.Text);

        await localBackend.SetTextAsync(remote.Text, token).ConfigureAwait(false);
    }

    private static ConfiguredPeer Resolve(LocalConfiguration config, string? deviceId) =>
        config.Resolve(deviceId) ?? throw new InvalidOperationException("No matching configured device.");

    private static async Task<MultiplexedProtocolPeer> ConnectAsync(ConfiguredPeer configuredPeer,
        DeviceIdentity identity, PeerTrustStore trust, CancellationToken token)
    {
        var connection = await MutualTlsTransport.ConnectAsync(configuredPeer.Host, configuredPeer.Port,
            identity, trust, configuredPeer.DeviceId, ConnectTimeout, token).ConfigureAwait(false);
        try
        {
            var frames = new BinaryFrameConnection(connection.Stream, leaveOpen: false);
            try
            {
                return new MultiplexedProtocolPeer(frames, (_, _) =>
                    ValueTask.FromException<ProtocolMessage>(new NotSupportedException("Clipboard client does not accept remote requests.")));
            }
            catch
            {
                await frames.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RequireCapabilityAsync(MultiplexedProtocolPeer protocol,
        string expectedDeviceId, CancellationToken token)
    {
        var reply = await protocol.RequestAsync("device.info", [], cancellationToken: token).ConfigureAwait(false);
        DeviceInfo? info;
        try { info = JsonSerializer.Deserialize<DeviceInfo>(reply.Payload); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid device.info response.", ex); }
        if (info is null || !string.Equals(info.DeviceId, expectedDeviceId, StringComparison.Ordinal))
            throw new InvalidDataException("The daemon returned device information for a different device.");
        if (info.Capabilities?.Any(c => c.Capability == Capability.Clipboard && c.Version >= 1) != true)
            throw new InvalidOperationException("Remote clipboard is not supported by this device.");
    }

    private static async Task<ClipboardPayload> GetRemoteClipboardAsync(MultiplexedProtocolPeer protocol,
        CancellationToken token)
    {
        var reply = await protocol.RequestAsync("clipboard.get", [], cancellationToken: token).ConfigureAwait(false);
        ClipboardPayload? payload;
        try { payload = JsonSerializer.Deserialize<ClipboardPayload>(reply.Payload, JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid clipboard.get response.", ex); }
        if (payload is null || string.IsNullOrWhiteSpace(payload.Origin) || payload.Text is null)
            throw new InvalidDataException("Incomplete clipboard.get response.");
        EnsureTextWithinLimit(payload.Text);
        return payload;
    }

    private static void EnsureTextWithinLimit(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
            throw new InvalidDataException("Clipboard text exceeds 256 KiB.");
    }
}
