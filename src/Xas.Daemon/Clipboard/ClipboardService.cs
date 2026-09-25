using System.Text;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Security;

namespace Xas.Daemon.Clipboard;

/// <summary>Authenticated text clipboard requests for the logged-in user session.</summary>
public sealed class ClipboardService(string localDeviceId, PeerPermissionStore permissions,
    ITextClipboardBackend backend)
{
    private const int MaxTextBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ulong? _importedChangeId;
    private string? _importedOrigin;
    private ulong _importedVersion;

    public bool IsAvailable => backend.IsAvailable;

    public async ValueTask<ProtocolMessage> HandleAsync(string peerId, ProtocolMessage request,
        CancellationToken cancellationToken)
    {
        if (!permissions.IsAllowed(peerId, Capability.Clipboard))
            throw new UnauthorizedAccessException("Clipboard access is not granted on this device for this peer.");
        if (!backend.IsAvailable) throw new PlatformNotSupportedException("A user-session text clipboard is unavailable.");
        return request.Method switch
        {
            "clipboard.get" => await GetAsync(request, cancellationToken).ConfigureAwait(false),
            "clipboard.set" => await SetAsync(peerId, request, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unknown clipboard method: {request.Method}")
        };
    }

    private async ValueTask<ProtocolMessage> GetAsync(ProtocolMessage request, CancellationToken token)
    {
        if (request.Payload.Length != 0) throw new InvalidDataException("clipboard.get has no request payload.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var snapshot = await backend.GetSnapshotAsync(token).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(snapshot.Text) > MaxTextBytes)
                throw new InvalidDataException("Clipboard text exceeds 256 KiB.");
            var imported = _importedChangeId == snapshot.ChangeId;
            var payload = new ClipboardPayload(imported ? _importedOrigin! : localDeviceId,
                imported ? _importedVersion : snapshot.ChangeId, snapshot.Text);
            return Reply(request, JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<ProtocolMessage> SetAsync(string peerId, ProtocolMessage request,
        CancellationToken token)
    {
        ClipboardPayload payload;
        try { payload = JsonSerializer.Deserialize<ClipboardPayload>(request.Payload, JsonOptions)
            ?? throw new InvalidDataException("Empty clipboard.set payload."); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid clipboard.set JSON.", ex); }
        if (payload.Origin != peerId || payload.Text is null)
            throw new InvalidDataException("clipboard.set origin must match the authenticated peer and text must be present.");
        if (Encoding.UTF8.GetByteCount(payload.Text) > MaxTextBytes)
            throw new InvalidDataException("Clipboard text exceeds 256 KiB.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await backend.SetTextAsync(payload.Text, token).ConfigureAwait(false);
            var after = await backend.GetSnapshotAsync(token).ConfigureAwait(false);
            _importedChangeId = after.ChangeId;
            _importedOrigin = peerId;
            _importedVersion = payload.Version;
            return Reply(request, []);
        }
        finally { _gate.Release(); }
    }

    private static ProtocolMessage Reply(ProtocolMessage request, byte[] payload) =>
        new(MessageKind.Response, request.RequestId, request.StreamId, request.Method, payload);
}
