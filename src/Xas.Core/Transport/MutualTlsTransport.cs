using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xas.Core.Security;

namespace Xas.Core.Transport;

/// <summary>A mutually authenticated TLS stream and the identity proved by its peer.</summary>
public sealed class AuthenticatedPeerConnection : IAsyncDisposable
{
    internal AuthenticatedPeerConnection(SslStream stream, string peerDeviceId)
        => (Stream, PeerDeviceId) = (stream, peerDeviceId);

    public SslStream Stream { get; }
    public string PeerDeviceId { get; }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>Creates TLS 1.2/1.3 TCP connections authenticated against locally approved device keys.</summary>
public static class MutualTlsTransport
{
    private const SslProtocols SupportedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>Connects and authenticates a peer. The returned connection owns the TCP socket.</summary>
    public static async Task<AuthenticatedPeerConnection> ConnectAsync(
        string host, int port, DeviceIdentity identity, PeerTrustStore trustStore,
        string expectedPeerDeviceId, TimeSpan handshakeTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trustStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPeerDeviceId);
        ValidateTimeout(handshakeTimeout);

        var client = new TcpClient();
        using var timeout = CreateTimeout(handshakeTimeout, cancellationToken);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return await AuthenticateClientAsync(client, identity, trustStore, host, expectedPeerDeviceId, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Authenticates an accepted TCP socket. The returned connection owns the socket.</summary>
    public static async Task<AuthenticatedPeerConnection> AcceptAsync(
        TcpClient client, DeviceIdentity identity, PeerTrustStore trustStore,
        TimeSpan handshakeTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trustStore);
        ValidateTimeout(handshakeTimeout);
        using var timeout = CreateTimeout(handshakeTimeout, cancellationToken);
        try
        {
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                var options = new SslServerAuthenticationOptions
                {
                    ServerCertificate = identity.Certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SupportedProtocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => trustStore.IsTrusted(certificate)
                };
                await ssl.AuthenticateAsServerAsync(options, timeout.Token).ConfigureAwait(false);
                var peerId = GetPeerDeviceId(ssl.RemoteCertificate);
                return new AuthenticatedPeerConnection(ssl, peerId);
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<AuthenticatedPeerConnection> AuthenticateClientAsync(
        TcpClient client, DeviceIdentity identity, PeerTrustStore trustStore,
        string host, string expectedPeerDeviceId, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            var certificates = new X509CertificateCollection { identity.Certificate };
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ClientCertificates = certificates,
                EnabledSslProtocols = SupportedProtocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => trustStore.IsTrusted(certificate)
            };
            await ssl.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
            var peerId = GetPeerDeviceId(ssl.RemoteCertificate);
            if (!string.Equals(peerId, expectedPeerDeviceId, StringComparison.Ordinal))
                throw new AuthenticationException("The authenticated peer does not match the intended device.");
            return new AuthenticatedPeerConnection(ssl, peerId);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }

    }

    private static string GetPeerDeviceId(X509Certificate? certificate)
    {
        if (certificate is null) throw new AuthenticationException("The peer did not provide a certificate.");
        using var cert = certificate is X509Certificate2 x509 ? new X509Certificate2(x509) : new X509Certificate2(certificate);
        var fingerprint = Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
        return "xas-" + fingerprint[..32].ToLowerInvariant();
    }

    private static CancellationTokenSource CreateTimeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite.");
    }
}
