using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Xas.Core.Security;

/// <summary>Identity and human verification for an untrusted, provisional pairing connection.</summary>
public static class PairingVerification
{
    public const int NonceBytes = 32;

    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    public static string DeviceId(string fingerprint)
    {
        var normalized = Normalize(fingerprint);
        return "xas-" + normalized[..32].ToLowerInvariant();
    }

    /// <summary>Validates only certificate shape; trust is established by comparing the code locally.</summary>
    public static bool IsProvisionalCertificate(X509Certificate2? certificate)
    {
        if (certificate is null) return false;
        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime()) return false;
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null) return rsa.KeySize >= 2048;
        using var ecdsa = certificate.GetECDsaPublicKey();
        return ecdsa is not null && ecdsa.KeySize >= 256;
    }

    /// <summary>Six digits bound to both TLS public keys and both fresh handshake nonces.</summary>
    public static string Code(string firstFingerprint, ReadOnlySpan<byte> firstNonce,
        string secondFingerprint, ReadOnlySpan<byte> secondNonce)
    {
        if (firstNonce.Length != NonceBytes || secondNonce.Length != NonceBytes)
            throw new ArgumentException($"Pairing nonces must be {NonceBytes} bytes.");
        var first = Convert.FromHexString(Normalize(firstFingerprint));
        var second = Convert.FromHexString(Normalize(secondFingerprint));
        var bytes = new byte["xas-pair-v1"u8.Length + 2 * (32 + NonceBytes)];
        "xas-pair-v1"u8.CopyTo(bytes);
        var offset = "xas-pair-v1"u8.Length;
        if (first.AsSpan().SequenceCompareTo(second) > 0)
        {
            (first, second) = (second, first);
            var secondNonceCopy = secondNonce.ToArray();
            secondNonce = firstNonce.ToArray();
            firstNonce = secondNonceCopy;
        }
        first.CopyTo(bytes, offset); offset += 32;
        firstNonce.CopyTo(bytes.AsSpan(offset)); offset += NonceBytes;
        second.CopyTo(bytes, offset); offset += 32;
        secondNonce.CopyTo(bytes.AsSpan(offset));
        var digest = SHA256.HashData(bytes);
        var number = BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000;
        return number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = new string(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        if (normalized.Length != 64) throw new ArgumentException("Expected a SHA-256 fingerprint.", nameof(value));
        return normalized;
    }
}
