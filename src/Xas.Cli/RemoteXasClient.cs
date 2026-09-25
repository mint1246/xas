using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
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
    private readonly bool _ownsIdentity;

    public RemoteXasClient(Stream output, Stream error)
        : this(new LocalConfiguration(),
            DeviceIdentity.LoadOrCreate(AppPaths.IdentityDirectory),
            new PeerTrustStore(AppPaths.TrustDirectory), output, error, ownsIdentity: true)
    {
    }

    public RemoteXasClient(LocalConfiguration configuration, DeviceIdentity identity,
        PeerTrustStore trust, Stream output, Stream error)
        : this(configuration, identity, trust, output, error, ownsIdentity: false)
    {
    }

    private RemoteXasClient(LocalConfiguration configuration, DeviceIdentity identity,
        PeerTrustStore trust, Stream output, Stream error, bool ownsIdentity)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
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
        var reply = await RequestAsync(configured, "shell.run", ShellWire.EncodeRequest(request), cancellationToken).ConfigureAwait(false);
        ShellResult result;
        try { result = ShellWire.DecodeResult(reply.Payload); }
        catch (InvalidDataException ex) { throw new XasClientException($"Invalid shell result from remote device: {ex.Message}"); }
        await _output.WriteAsync(result.StandardOutput, cancellationToken).ConfigureAwait(false);
        await _error.WriteAsync(result.StandardError, cancellationToken).ConfigureAwait(false);
        if (result.OutputTruncated)
        {
            var warning = System.Text.Encoding.UTF8.GetBytes("Warning: remote output was truncated.\n");
            await _error.WriteAsync(warning, cancellationToken).ConfigureAwait(false);
        }
        return result.ExitCode;
    }

    private ConfiguredPeer? Resolve(string? deviceId)
    {
        try { return _configuration.Resolve(deviceId); }
        catch (InvalidOperationException ex) { throw new XasClientException(ex.Message); }
    }

    private async Task<ProtocolMessage> RequestAsync(ConfiguredPeer configured, string method,
        byte[] payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            await using var tls = await MutualTlsTransport.ConnectAsync(configured.Host, configured.Port,
                _identity, _trust, configured.DeviceId, RequestTimeout, timeout.Token).ConfigureAwait(false);
            await using var frames = new BinaryFrameConnection(tls.Stream, leaveOpen: true);
            await using var peer = new MultiplexedProtocolPeer(frames, (_, _) =>
                ValueTask.FromException<ProtocolMessage>(new NotSupportedException("The CLI client does not accept remote requests.")));
            return await peer.RequestAsync(method, payload, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XasClientException($"Timed out connecting to device {configured.DeviceId}.");
        }
        catch (RemoteProtocolException ex) { throw new XasClientException(ex.Message); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or
            System.Security.Authentication.AuthenticationException)
        {
            throw new XasClientException($"Could not communicate with device {configured.DeviceId}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsIdentity) _identity.Dispose();
    }
}
