using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Xas.Core.Security;

/// <summary>A persistent public-key identity and the self-signed certificate used for TLS.</summary>
public sealed class DeviceIdentity : IDisposable
{
    private DeviceIdentity(string deviceId, string fingerprint, X509Certificate2 certificate)
        => (DeviceId, Fingerprint, Certificate) = (deviceId, fingerprint, certificate);

    public string DeviceId { get; }
    /// <summary>SHA-256 of the DER SubjectPublicKeyInfo, in uppercase hexadecimal.</summary>
    public string Fingerprint { get; }
    public X509Certificate2 Certificate { get; }

    public static DeviceIdentity LoadOrCreate(string directory, string displayName = "Xas device")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "device-identity.pfx");
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows()) bytes = Dpapi.Unprotect(bytes);
            var existing = X509CertificateLoader.LoadPkcs12(bytes, (string?)null, KeyStorageFlags);
            CryptographicOperations.ZeroMemory(bytes);
            return FromCertificate(existing);
        }

        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={SanitizeCommonName(displayName)}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new System.Security.Cryptography.OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"), // TLS server authentication
                new("1.3.6.1.5.5.7.3.2")  // TLS client authentication
            }, false));
        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
        var pfx = cert.Export(X509ContentType.Pfx);
        var stored = OperatingSystem.IsWindows() ? Dpapi.Protect(pfx) : pfx;
        WritePrivateFile(path, stored);
        if (!ReferenceEquals(pfx, stored)) CryptographicOperations.ZeroMemory(pfx);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return FromCertificate(X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), (string?)null, KeyStorageFlags));
    }

    // Schannel cannot use an ephemeral private key for TLS authentication on Windows. The encrypted
    // PFX remains the durable identity, but the runtime certificate must be imported into the current
    // user's key store so SslStream can acquire its private key. Linux can keep the key ephemeral.
    private static X509KeyStorageFlags KeyStorageFlags => OperatingSystem.IsWindows()
        ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable
        : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;

    private static DeviceIdentity FromCertificate(X509Certificate2 cert)
    {
        var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        var fingerprint = Convert.ToHexString(hash);
        return new DeviceIdentity("xas-" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant(), fingerprint, cert);
    }

    private static string SanitizeCommonName(string name)
    {
        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').Take(64).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "Xas device" : clean;
    }

    private static void WritePrivateFile(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
        stream.Flush(true);
    }

    public void Dispose() => Certificate.Dispose();
}

/// <summary>Stable, human-comparable display forms. Pairing must still be explicitly approved.</summary>
public static class PairingFingerprint
{
    public static string Format(string fingerprint)
    {
        var hex = new string(fingerprint.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        if (hex.Length != 64) throw new ArgumentException("Expected a SHA-256 fingerprint.", nameof(fingerprint));
        return string.Join(" ", Enumerable.Range(0, 8).Select(i => hex.Substring(i * 8, 8)));
    }

    /// <summary>A short confirmation code derived from both identities; compare out of band.</summary>
    public static string ConfirmationCode(string localFingerprint, string remoteFingerprint)
    {
        var pair = new[] { Normalize(localFingerprint), Normalize(remoteFingerprint) };
        Array.Sort(pair, StringComparer.Ordinal);
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(pair[0] + pair[1]));
        var number = (uint)(digest[0] << 16 | digest[1] << 8 | digest[2]);
        return (number % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Normalize(string value)
    {
        var hex = new string(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        if (hex.Length != 64) throw new ArgumentException("Expected a SHA-256 fingerprint.");
        return hex;
    }
}

/// <summary>Local trust database. Approve/Revoke are intended to be called only by local pairing UI.</summary>
public sealed class PeerTrustStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, TrustedPeer> _peers;

    public PeerTrustStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "trusted-peers.json");
        _peers = Load(_path);
    }

    public IReadOnlyList<TrustedPeer> List() { lock (_gate) { Refresh(); return _peers.Values.OrderBy(p => p.DeviceId).ToArray(); } }

    public void Approve(string deviceId, string fingerprint, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var normalized = NormalizeFingerprint(fingerprint);
        var expectedId = "xas-" + normalized[..32].ToLowerInvariant();
        if (!string.Equals(deviceId, expectedId, StringComparison.Ordinal))
            throw new ArgumentException("Device ID does not match the approved public-key fingerprint.", nameof(deviceId));
        lock (_gate)
        {
            Refresh();
            _peers[deviceId] = new TrustedPeer(deviceId, normalized, displayName, DateTimeOffset.UtcNow);
            Save();
        }
    }

    public bool Revoke(string deviceId)
    {
        lock (_gate) { Refresh(); var removed = _peers.Remove(deviceId); if (removed) Save(); return removed; }
    }

    public bool IsTrusted(X509Certificate? certificate)
    {
        if (certificate is null) return false;
        using var cert = certificate as X509Certificate2 is { } x ? new X509Certificate2(x) : new X509Certificate2(certificate);
        var now = DateTime.UtcNow;
        if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime()) return false;
        var fp = Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
        var id = "xas-" + fp[..32].ToLowerInvariant();
        lock (_gate) { Refresh(); return _peers.TryGetValue(id, out var peer) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(peer.Fingerprint), Convert.FromHexString(fp)); }
    }

    /// <summary>Use for SslClientAuthenticationOptions.RemoteCertificateValidationCallback.</summary>
    public RemoteCertificateValidationCallback ValidationCallback => (_, certificate, _, _) => IsTrusted(certificate);

    private static string NormalizeFingerprint(string value)
    {
        var hex = new string(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        if (hex.Length != 64) throw new ArgumentException("Expected a SHA-256 fingerprint.", nameof(value));
        return hex;
    }

    private static Dictionary<string, TrustedPeer> Load(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, TrustedPeer>>(File.ReadAllBytes(path)) ?? new(StringComparer.Ordinal); }
        catch (System.Text.Json.JsonException ex) { throw new InvalidDataException("The peer trust store is invalid; refusing to trust peers.", ex); }
    }

    private void Refresh() => _peers = Load(_path);

    private void Save()
    {
        var temp = _path + ".tmp";
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_peers);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(temp, _path, true);
    }
}

public sealed record TrustedPeer(string DeviceId, string Fingerprint, string DisplayName, DateTimeOffset ApprovedAtUtc);

internal static class Dpapi
{
    private const int UiForbidden = 0x1;
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] data) => Transform(data, true);
    public static byte[] Unprotect(byte[] data) => Transform(data, false);
    private static byte[] Transform(byte[] data, bool protect)
    {
        var inputPtr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, inputPtr, data.Length);
        var input = new Blob { Size = data.Length, Data = inputPtr };
        try
        {
            var ok = protect ? CryptProtectData(ref input, "Xas device identity", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { _ = LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(inputPtr); }
    }
}
