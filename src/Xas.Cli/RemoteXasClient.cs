using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Discovery;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Services;
using Xas.Core.Transport;

namespace Xas.Cli;

/// <summary>CLI client for configured, explicitly trusted remote XAS daemons.</summary>
public sealed class RemoteXasClient : IXasClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly LocalConfiguration _configuration;
    private readonly DeviceIdentity _identity;
    private readonly PeerTrustStore _trust;
    private readonly Stream _output;
    private readonly Stream _error;
    private readonly Stream _input;
    private readonly bool _ownsIdentity;

    public RemoteXasClient(Stream output, Stream error)
        : this(new LocalConfiguration(),
            DeviceIdentity.LoadOrCreate(AppPaths.IdentityDirectory),
            new PeerTrustStore(AppPaths.TrustDirectory), output, error,
            Console.IsInputRedirected ? Console.OpenStandardInput() : Stream.Null, ownsIdentity: true)
    {
    }

    public RemoteXasClient(LocalConfiguration configuration, DeviceIdentity identity,
        PeerTrustStore trust, Stream output, Stream error, Stream? input = null)
        : this(configuration, identity, trust, output, error, input ?? Stream.Null, ownsIdentity: false)
    {
    }

    private RemoteXasClient(LocalConfiguration configuration, DeviceIdentity identity,
        PeerTrustStore trust, Stream output, Stream error, Stream input, bool ownsIdentity)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _ownsIdentity = ownsIdentity;
    }

    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DeviceInfo> peers = _configuration.Peers
            .Select(p => new DeviceInfo(p.DeviceId, p.Name, "Unknown", "Unknown", Array.Empty<CapabilityVersion>()))
            .ToArray();
        return Task.FromResult(peers);
    }

    public async Task<DeviceInfo?> GetDeviceInfoAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var configured = Resolve(deviceId);
        if (configured is null) return null;
        var reply = await RequestAsync(configured, "device.info", Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
        try
        {
            var info = JsonSerializer.Deserialize<DeviceInfo>(reply.Payload);
            if (info is null || !string.Equals(info.DeviceId, configured.DeviceId, StringComparison.Ordinal))
                throw new XasClientException("The daemon returned device information for a different device.");
            return info;
        }
        catch (JsonException ex) { throw new XasClientException($"Invalid device information response: {ex.Message}"); }
    }

    public Task SetDefaultDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { _configuration.SetDefault(deviceId); }
        catch (InvalidOperationException ex) { throw new XasClientException(ex.Message); }
        return Task.CompletedTask;
    }

    public async Task<bool> PingAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var configured = Resolve(deviceId);
        if (configured is null) return false;
        try
        {
            var reply = await RequestAsync(configured, "device.ping", Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
            return reply.Payload.Length == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or
            System.Security.Authentication.AuthenticationException or TimeoutException or XasClientException)
        {
            return false;
        }
    }

    public async Task<int> RunShellAsync(ShellRequest request, string? deviceId, CancellationToken cancellationToken)
    {
        var configured = Resolve(deviceId) ?? throw new XasClientException("No matching configured device.");
        var info = await GetDeviceInfoAsync(configured.DeviceId, cancellationToken).ConfigureAwait(false);
        configured = Resolve(configured.DeviceId) ?? configured;
        var shellVersion = info?.Capabilities.FirstOrDefault(c => c.Capability == Capability.Shell)?.Version ?? 0;
        if (shellVersion == 0) throw new XasClientException("Remote shell is not supported by this device.");
        var streamEnds = 0;
        async ValueTask OnStream(ProtocolMessage message)
        {
            if (message.Method != "shell.stream") throw new InvalidDataException("Unexpected stream method.");
            if (message.Kind == MessageKind.StreamEnd)
            {
                streamEnds |= message.StreamId switch { 1 => 1, 2 => 2, _ => throw new InvalidDataException("Unknown shell stream.") };
                return;
            }
            if (message.Kind != MessageKind.StreamData) throw new InvalidDataException("Unexpected shell message.");
            var destination = message.StreamId switch
            {
                1 => _output,
                2 => _error,
                _ => throw new InvalidDataException("Unknown shell stream.")
            };
            await destination.WriteAsync(message.Payload, cancellationToken).ConfigureAwait(false);
        }
        var method = shellVersion >= 2 ? "shell.stream" : "shell.run";
        var inputBytes = shellVersion >= 2 ? await ReadPipedInputAsync(cancellationToken).ConfigureAwait(false) : [];
        var payload = shellVersion >= 2 ? ShellWire.EncodeInvocation(request, inputBytes) : ShellWire.EncodeRequest(request);
        var reply = await RequestAsync(configured, method, payload, cancellationToken,
            shellVersion >= 2 ? OnStream : null).ConfigureAwait(false);
        ShellResult result;
        try { result = ShellWire.DecodeResult(reply.Payload); }
        catch (InvalidDataException ex) { throw new XasClientException($"Invalid shell result from remote device: {ex.Message}"); }
        if (shellVersion >= 2 && streamEnds != 3) throw new XasClientException("Remote shell streams ended unexpectedly.");
        await _output.WriteAsync(result.StandardOutput, cancellationToken).ConfigureAwait(false);
        await _error.WriteAsync(result.StandardError, cancellationToken).ConfigureAwait(false);
        if (result.OutputTruncated)
        {
            var warning = System.Text.Encoding.UTF8.GetBytes("Warning: remote output was truncated.\n");
            await _error.WriteAsync(warning, cancellationToken).ConfigureAwait(false);
        }
        return result.ExitCode;
    }

    private async Task<byte[]> ReadPipedInputAsync(CancellationToken cancellationToken)
    {
        const int limit = 900_000;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await _input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > limit) throw new XasClientException("Piped standard input exceeds the current 900,000-byte limit.");
            buffer.Write(chunk, 0, read);
        }
    }

    private ConfiguredPeer? Resolve(string? deviceId)
    {
        try { return _configuration.Resolve(deviceId); }
        catch (InvalidOperationException ex) { throw new XasClientException(ex.Message); }
    }

    private async Task<ProtocolMessage> RequestAsync(ConfiguredPeer configured, string method,
        byte[] payload, CancellationToken cancellationToken, Func<ProtocolMessage, ValueTask>? onMessage = null)
    {
        AuthenticatedPeerConnection connection;
        ConfiguredPeer? discoveredEndpoint = null;
        try
        {
            connection = await ConnectAsync(configured, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectFailure(ex, cancellationToken))
        {
            var discovered = await DiscoverPeerAsync(configured, cancellationToken).ConfigureAwait(false);
            discoveredEndpoint = discovered;
            try
            {
                connection = await ConnectAsync(discovered, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryError) when (IsConnectFailure(retryError, cancellationToken))
            {
                throw new XasClientException($"Could not authenticate or connect to device {configured.DeviceId} at its configured or discovered endpoint: {retryError.Message}");
            }
        }

        await using (connection.ConfigureAwait(false))
        {
            try
            {
                await using var frames = new BinaryFrameConnection(connection.Stream, leaveOpen: true);
                await using var peer = new MultiplexedProtocolPeer(frames, (_, _) =>
                    ValueTask.FromException<ProtocolMessage>(new NotSupportedException("The CLI client does not accept remote requests.")));
                if (onMessage is not null) peer.MessageReceived += onMessage;
                using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (method is "device.info" or "device.ping") responseTimeout.CancelAfter(RequestTimeout);
                var reply = await peer.RequestAsync(method, payload, cancellationToken: responseTimeout.Token).ConfigureAwait(false);
                // The UDP record is only a candidate. Persist it after pinned TLS and a successful daemon reply.
                if (discoveredEndpoint is not null) _configuration.UpsertPeer(discoveredEndpoint);
                return reply;
            }
            catch (RemoteProtocolException ex) { throw new XasClientException(ex.Message); }
            catch (InvalidDataException ex) { throw new XasClientException($"Invalid response from device {configured.DeviceId}: {ex.Message}"); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new XasClientException($"Timed out waiting for device {configured.DeviceId}.");
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                throw new XasClientException($"Could not communicate with device {configured.DeviceId}: {ex.Message}");
            }
        }
    }

    private async Task<AuthenticatedPeerConnection> ConnectAsync(ConfiguredPeer peer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        return await MutualTlsTransport.ConnectAsync(peer.Host, peer.Port,
            _identity, _trust, peer.DeviceId, RequestTimeout, timeout.Token).ConfigureAwait(false);
    }

    private async Task<ConfiguredPeer> DiscoverPeerAsync(ConfiguredPeer configured, CancellationToken cancellationToken)
    {
        await using var discovery = new LanDiscoveryService(_identity.DeviceId, Environment.MachineName);
        try { await discovery.StartAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            throw new XasClientException($"Could not start LAN discovery for device {configured.DeviceId}: {ex.Message}");
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var peer = discovery.GetPeers().FirstOrDefault(p =>
                string.Equals(p.DeviceId, configured.DeviceId, StringComparison.Ordinal));
            if (peer is not null)
                return configured with { Host = peer.Address, Port = peer.TcpPort };
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
        }
        throw new XasClientException($"Device {configured.DeviceId} was not found on the LAN.");
    }

    private static bool IsConnectFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is
            (IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException);

    public void Dispose()
    {
        if (_ownsIdentity) _identity.Dispose();
    }
}
