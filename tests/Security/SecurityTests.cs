using Xas.Core.Security;

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

            var trust = new PeerTrustStore(directory);
            if (trust.IsTrusted(identity.Certificate)) throw new InvalidOperationException("A peer was trusted before approval.");
            trust.Approve(identity.DeviceId, identity.Fingerprint, "test device");
            if (!trust.IsTrusted(identity.Certificate)) throw new InvalidOperationException("Approved certificate was not trusted.");
            var reloadedTrust = new PeerTrustStore(directory);
            if (!reloadedTrust.IsTrusted(identity.Certificate)) throw new InvalidOperationException("Trust did not persist.");
            if (!reloadedTrust.Revoke(identity.DeviceId) || reloadedTrust.IsTrusted(identity.Certificate))
                throw new InvalidOperationException("Revocation did not take effect.");
            return Task.CompletedTask;
        }
        finally { Directory.Delete(directory, true); }
    }
}
