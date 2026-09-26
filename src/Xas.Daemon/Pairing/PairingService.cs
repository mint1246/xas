using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xas.Core.Security;

namespace Xas.Daemon.Pairing;

/// <summary>Short lived, untrusted bootstrap sessions. A key is pinned only after both local users approve.</summary>
public sealed class PairingService : IAsyncDisposable
{
    private const int MaxPending = 32;
    private const int MaxMessageBytes = 4096;
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(2);
    private readonly DeviceIdentity _identity;
    private readonly PeerTrustStore _trust;
    private readonly int _port;
    private readonly string _localName;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public PairingService(DeviceIdentity identity, PeerTrustStore trust, int port, string localName)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        if (port is < 2 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
        _localName = string.IsNullOrWhiteSpace(localName) ? "Xas device" : localName;
    }

    /// <summary>Starts the inbound provisional pairing listener.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_listener is not null) throw new InvalidOperationException("Pairing listener is already running.");
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_listener, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Opens an untrusted pairing session to a discovered or manually specified endpoint.</summary>
    public async Task<PairingPending> BeginAsync(string host, int pairingPort, string? expectedDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (pairingPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(pairingPort));
        if (_sessions.Count >= MaxPending) throw new InvalidOperationException("Too many pairing requests are pending.");

        var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            timeout.CancelAfter(PairingLifetime);
            await client.ConnectAsync(host, pairingPort, timeout.Token).ConfigureAwait(false);
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ClientCertificates = new X509CertificateCollection { _identity.Certificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => IsProvisional(certificate)
                }, timeout.Token).ConfigureAwait(false);
                var remote = GetRemote(ssl.RemoteCertificate);
                if (expectedDeviceId is not null && !string.Equals(expectedDeviceId, remote.DeviceId, StringComparison.Ordinal))
                    throw new AuthenticationException("The endpoint certificate does not match the discovered device ID.");
                var localNonce = RandomNumberGenerator.GetBytes(PairingVerification.NonceBytes);
                await WriteMessageAsync(ssl, new Hello(_identity.DeviceId, _localName, Convert.ToBase64String(localNonce), _port - 1), timeout.Token).ConfigureAwait(false);
                var remoteHello = await ReadMessageAsync<Hello>(ssl, timeout.Token).ConfigureAwait(false);
                var remoteNonce = ParseNonce(remoteHello.Nonce);
                ValidateHello(remoteHello, remote);
                remote = remote with
                {
                    Name = remoteHello.Name,
                    Address = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? host,
                    ControlPort = remoteHello.ControlPort
                };
                var code = PairingVerification.Code(_identity.Fingerprint, localNonce, remote.Fingerprint, remoteNonce);
                var pending = new PairingPending(Guid.NewGuid().ToString("N"), remote.DeviceId, remoteHello.Name,
                    remote.Fingerprint, code, DateTimeOffset.UtcNow, Incoming: false, remote.Address, remote.ControlPort);
                var session = new Session(pending, ssl, client, _trust, remote, _stop.Token);
                if (!_sessions.TryAdd(pending.PairingId, session)) throw new InvalidOperationException("Could not register pairing request.");
                session.Completion = CompleteSessionAsync(session);
                return pending;
            }
            catch { await ssl.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch { client.Dispose(); throw; }
    }

    public IReadOnlyList<PairingPending> ListPending() => _sessions.Values
        .Select(s => s.Pending).OrderBy(p => p.CreatedAtUtc).ToArray();

    /// <summary>Records this device's local decision and waits for the other device's decision.</summary>
    public async Task<bool> ApproveAsync(string pairingId, bool approve, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(pairingId, out var session)) throw new KeyNotFoundException("Pairing request not found or expired.");
        if (!session.LocalDecision.TrySetResult(approve)) throw new InvalidOperationException("This pairing request already has a local decision.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        return session.Completion is { } completion && await completion.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        foreach (var session in _sessions.Values) session.Client.Dispose();
        var completions = _sessions.Values.Select(s => s.Completion).Where(t => t is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(completions).ConfigureAwait(false); } catch { }
        _sessions.Clear();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        while (!linked.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (linked.IsCancellationRequested) { break; }
            if (_sessions.Count >= MaxPending) { client.Dispose(); continue; }
            _ = HandleIncomingAsync(client, linked.Token);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken cancellationToken)
    {
        SslStream? ssl = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PairingLifetime);
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _identity.Certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => IsProvisional(certificate)
            }, timeout.Token).ConfigureAwait(false);
            var remote = GetRemote(ssl.RemoteCertificate);
            var remoteHello = await ReadMessageAsync<Hello>(ssl, timeout.Token).ConfigureAwait(false);
            ValidateHello(remoteHello, remote);
            var remoteNonce = ParseNonce(remoteHello.Nonce);
            var localNonce = RandomNumberGenerator.GetBytes(PairingVerification.NonceBytes);
            await WriteMessageAsync(ssl, new Hello(_identity.DeviceId, _localName, Convert.ToBase64String(localNonce), _port - 1), timeout.Token).ConfigureAwait(false);
            remote = remote with
            {
                Name = remoteHello.Name,
                Address = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? string.Empty,
                ControlPort = remoteHello.ControlPort
            };
            var code = PairingVerification.Code(_identity.Fingerprint, localNonce, remote.Fingerprint, remoteNonce);
            var pending = new PairingPending(Guid.NewGuid().ToString("N"), remote.DeviceId, remoteHello.Name,
                remote.Fingerprint, code, DateTimeOffset.UtcNow, Incoming: true, remote.Address, remote.ControlPort);
            var session = new Session(pending, ssl, client, _trust, remote, cancellationToken);
            if (!_sessions.TryAdd(pending.PairingId, session)) throw new InvalidOperationException("Could not register pairing request.");
            ssl = null;
            session.Completion = CompleteSessionAsync(session);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or JsonException or FormatException)
        {
            if (ssl is not null) await ssl.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
        }
    }

    private async Task<bool> CompleteSessionAsync(Session session)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime);
        timeout.CancelAfter(PairingLifetime);
        try
        {
            var localApproved = await session.LocalDecision.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            await WriteMessageAsync(session.Stream, new Decision(localApproved), timeout.Token).ConfigureAwait(false);
            var remoteDecision = await ReadMessageAsync<Decision>(session.Stream, timeout.Token).ConfigureAwait(false);
            if (!localApproved || !remoteDecision.Approved) return false;
            // Persist only after the peer's approval has crossed the TLS channel.
            session.Trust.Approve(session.Remote.DeviceId, session.Remote.Fingerprint, session.Remote.Name);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or AuthenticationException or JsonException)
        {
            // Disconnects, malformed messages and expiry all fail closed without pinning.
            return false;
        }
        finally
        {
            _sessions.TryRemove(session.Pending.PairingId, out _);
            session.Client.Dispose();
        }
    }

    private static RemoteInfo GetRemote(X509Certificate? certificate)
    {
        if (certificate is null) throw new AuthenticationException("The peer did not provide a certificate.");
        using var cert = ToCertificate(certificate) ?? throw new AuthenticationException("Invalid peer certificate.");
        if (!PairingVerification.IsProvisionalCertificate(cert)) throw new AuthenticationException("The peer certificate is not valid for pairing.");
        var fingerprint = PairingVerification.Fingerprint(cert);
        return new RemoteInfo(PairingVerification.DeviceId(fingerprint), fingerprint, cert.GetNameInfo(X509NameType.SimpleName, false));
    }

    private static X509Certificate2? ToCertificate(X509Certificate? certificate) => certificate is null ? null :
        certificate is X509Certificate2 cert ? new X509Certificate2(cert) : new X509Certificate2(certificate);

    private static bool IsProvisional(X509Certificate? certificate)
    {
        using var cert = ToCertificate(certificate);
        return PairingVerification.IsProvisionalCertificate(cert);
    }

    private static byte[] ParseNonce(string value)
    {
        var nonce = Convert.FromBase64String(value);
        if (nonce.Length != PairingVerification.NonceBytes) throw new AuthenticationException("Invalid pairing nonce.");
        return nonce;
    }

    private static void ValidateHello(Hello hello, RemoteInfo remote)
    {
        if (hello.DeviceId != remote.DeviceId) throw new AuthenticationException("Pairing identity did not match its TLS certificate.");
        if (string.IsNullOrWhiteSpace(hello.Name) || hello.Name.Length > 128 || hello.Name.Any(char.IsControl))
            throw new AuthenticationException("Invalid pairing device name.");
        if (hello.ControlPort is < 1 or >= 65535) throw new AuthenticationException("Invalid pairing control port.");
    }

    private static async Task WriteMessageAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("Pairing message is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<T> ReadMessageAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaxMessageBytes) throw new InvalidDataException("Invalid pairing message length.");
        var bytes = new byte[length];
        await ReadExactlyAsync(stream, bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Invalid pairing message.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        while (!buffer.IsEmpty)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Pairing connection closed.");
            buffer = buffer[read..];
        }
    }

    private sealed record Hello(string DeviceId, string Name, string Nonce, int ControlPort);
    private sealed record Decision(bool Approved);
    private sealed record RemoteInfo(string DeviceId, string Fingerprint, string Name, string Address = "", int ControlPort = 0);

    private sealed class Session(PairingPending pending, SslStream stream, TcpClient client,
        PeerTrustStore trust, RemoteInfo remote, CancellationToken lifetime)
    {
        public PairingPending Pending { get; } = pending;
        public SslStream Stream { get; } = stream;
        public TcpClient Client { get; } = client;
        public PeerTrustStore Trust { get; } = trust;
        public RemoteInfo Remote { get; } = remote;
        public CancellationToken Lifetime { get; } = lifetime;
        public TaskCompletionSource<bool> LocalDecision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool>? Completion { get; set; }
    }
}

public sealed record PairingPending(string PairingId, string DeviceId, string DisplayName,
    string Fingerprint, string Code, DateTimeOffset CreatedAtUtc, bool Incoming, string Address, int ControlPort);
