using Xas.Core.Configuration;
using Xas.Core.Security;
using Xas.Daemon.Sessions;

namespace Xas.Daemon;

/// <summary>Local-only mutations that update persistent peer state and live connections together.</summary>
internal sealed class PeerAdministrationService(LocalConfiguration configuration, PeerTrustStore trust,
    PeerPermissionStore permissions, PeerSessionManager sessions)
{
    public async Task<string> RevokeAsync(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var configured = configuration.Resolve(query);
        var deviceId = configured?.DeviceId ?? ResolveTrusted(trust.List(), query)?.DeviceId
            ?? throw new InvalidOperationException("No matching paired device.");

        configuration.RemovePeer(deviceId);
        permissions.RemovePeer(deviceId);
        trust.Revoke(deviceId);
        await sessions.RemovePeerAsync(deviceId).ConfigureAwait(false);
        return deviceId;
    }

    private static TrustedPeer? ResolveTrusted(IReadOnlyList<TrustedPeer> peers, string query)
    {
        var matches = peers.Where(p => p.DeviceId.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            p.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException($"Ambiguous device ID: {query}");
        return matches.SingleOrDefault();
    }
}
