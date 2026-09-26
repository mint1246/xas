using Xas.Core.Security;
using Xas.Core.Configuration;
using Xas.Core;
using Xas.Core.Privileged;

namespace Xas.Tests;

public static class SecurityTests
{
    public static Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xas-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var identity = DeviceIdentity.LoadOrCreate(directory, "test device");
            using var reloaded = DeviceIdentity.LoadOrCreate(directory, "ignored on reload");
            if (identity.DeviceId != reloaded.DeviceId || identity.Fingerprint != reloaded.Fingerprint)
                throw new InvalidOperationException("Identity did not persist across reload.");
            if (PairingFingerprint.ConfirmationCode(identity.Fingerprint, reloaded.Fingerprint) !=
                PairingFingerprint.ConfirmationCode(reloaded.Fingerprint, identity.Fingerprint))
                throw new InvalidOperationException("Pairing code is not symmetric.");

            var leftNonce = Enumerable.Range(0, PairingVerification.NonceBytes).Select(i => (byte)i).ToArray();
            var rightNonce = Enumerable.Range(0, PairingVerification.NonceBytes).Select(i => (byte)(255 - i)).ToArray();
            using var peerIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(directory, "peer"), "peer device");
            var code = PairingVerification.Code(identity.Fingerprint, leftNonce, peerIdentity.Fingerprint, rightNonce);
            if (code != PairingVerification.Code(peerIdentity.Fingerprint, rightNonce, identity.Fingerprint, leftNonce))
                throw new InvalidOperationException("Pairing code differs between the two peers.");
            var changedNonce = rightNonce.ToArray();
            changedNonce[0] ^= 0x80;
            if (code == PairingVerification.Code(identity.Fingerprint, leftNonce, peerIdentity.Fingerprint, changedNonce))
                throw new InvalidOperationException("Pairing code did not bind the fresh handshake nonces.");
            if (PairingVerification.DeviceId(identity.Fingerprint) != identity.DeviceId)
                throw new InvalidOperationException("Device ID is not derived from the public key.");
            if (!PairingVerification.IsProvisionalCertificate(identity.Certificate))
                throw new InvalidOperationException("A generated identity certificate is not valid for provisional pairing.");

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    WindowsAdminBrokerClient.VerifyBrokerServiceProcessId((uint)Environment.ProcessId);
                    throw new InvalidOperationException("An arbitrary local process was accepted as the administrator broker service.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            var trust = new PeerTrustStore(directory);
            if (trust.IsTrusted(identity.Certificate)) throw new InvalidOperationException("A peer was trusted before approval.");
            trust.Approve(identity.DeviceId, identity.Fingerprint, "test device");
            if (!trust.IsTrusted(identity.Certificate)) throw new InvalidOperationException("Approved certificate was not trusted.");
            var reloadedTrust = new PeerTrustStore(directory);
            if (!reloadedTrust.IsTrusted(identity.Certificate)) throw new InvalidOperationException("Trust did not persist.");
            if (!reloadedTrust.Revoke(identity.DeviceId) || reloadedTrust.IsTrusted(identity.Certificate))
                throw new InvalidOperationException("Revocation did not take effect.");
            if (trust.IsTrusted(identity.Certificate))
                throw new InvalidOperationException("An already-running trust store missed external revocation.");

            var permissions = new PeerPermissionStore(directory);
            if (permissions.IsAllowed(identity.DeviceId, Capability.Shell))
                throw new InvalidOperationException("Shell access was granted by default.");
            permissions.SetAllowed(identity.DeviceId, Capability.Shell, true);
            var otherPermissions = new PeerPermissionStore(directory);
            if (!otherPermissions.IsAllowed(identity.DeviceId, Capability.Shell))
                throw new InvalidOperationException("Local grant did not persist.");
            permissions.RemovePeer(identity.DeviceId);
            if (otherPermissions.IsAllowed(identity.DeviceId, Capability.Shell))
                throw new InvalidOperationException("Removed peer retained shell access.");

            var settings = new LocalConfiguration(directory);
            settings.UpsertPeer(new ConfiguredPeer(identity.DeviceId, "test device", "127.0.0.1", 47821));
            settings.SetDefault(identity.DeviceId[..8]);
            if (settings.Resolve(null)?.DeviceId != identity.DeviceId)
                throw new InvalidOperationException("Default peer did not resolve.");
            settings.RemovePeer(identity.DeviceId);
            if (new LocalConfiguration(directory).Resolve(null) is not null)
                throw new InvalidOperationException("Removed peer remained the default.");
            return Task.CompletedTask;
        }
        finally { Directory.Delete(directory, true); }
    }
}
