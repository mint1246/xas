using System.Net;
using System.Net.Sockets;
using Xas.Core;
using Xas.Core.Discovery;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Transport;
using Xas.Daemon.Interactive;
using Xas.Daemon.FileTransfer;
using Xas.Daemon.Input;
using Xas.Daemon.Sessions;
using Xas.Daemon.Pairing;
using Xas.Daemon.Web;
using Xas.Daemon.LocalIpc;
using Xas.Daemon.FileSystem.Mount;
using Xas.Input;

namespace Xas.Daemon;

public sealed class DaemonHost
{
    private readonly DeviceIdentity _identity;
    private readonly PeerTrustStore _trust;
    private readonly PeerPermissionStore _permissions;
    private readonly int _port;
    private readonly int _webPort;
    private readonly bool _enableNativeOrchestration;
    private readonly LocalConfiguration _configuration;
    private readonly InputControlService _input;
    private readonly RequestDispatcher _dispatcher;
    public PeerSessionManager PeerSessions { get; }

    public DaemonHost(DeviceIdentity identity, PeerTrustStore trust, PeerPermissionStore permissions,
        int port = XasProtocol.DefaultPort, IInputInjectionBackend? inputBackend = null,
        ITextClipboardBackend? clipboardBackend = null, IInputPipelineMetrics? inputMetrics = null,
        LocalConfiguration? configuration = null, int webPort = 47832, bool enableNativeOrchestration = true)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _port = port;
        _webPort = webPort;
        _enableNativeOrchestration = enableNativeOrchestration;
        _configuration = configuration ?? new LocalConfiguration();
        _input = new InputControlService(permissions,
            inputBackend ?? (OperatingSystem.IsWindows() ? new WindowsSendInputBackend() :
                OperatingSystem.IsLinux() ? CreateLinuxInputBackend() : new UnavailableInputBackend()), inputMetrics);
        _dispatcher = new RequestDispatcher(identity, permissions, () => _input.ProtocolVersion, clipboardBackend, _configuration);
        PeerSessions = new PeerSessionManager(identity, trust, permissions, _input, _dispatcher, _configuration, port);
    }

    /// <summary>
    /// Picks a Linux injection backend. uinput comes first because it needs no portal, so handoff never
    /// asks the user for consent, and it is the only option that works identically on X11 and Wayland. The
    /// X11 and Wayland portal paths remain as fallbacks for a host where /dev/uinput is not writable.
    /// </summary>
    private static IInputInjectionBackend CreateLinuxInputBackend()
    {
        var display = new Display.LinuxDisplayMetadataService();
        IInputInjectionBackend? uinput = new LinuxUinputInputBackend(() =>
            display.GetPrimaryDisplayAsync().GetAwaiter().GetResult());
        if (uinput.IsAvailable) return uinput;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return new LinuxWaylandInputBackend();
        if (Environment.GetEnvironmentVariable("XAS_ENABLE_X11_INPUT") == "1")
            return new LinuxX11InputBackend();
        return new UnavailableInputBackend();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        await using var sessions = PeerSessions;
        await using var pairing = new PairingService(_identity, _trust, checked(_port + 1), Environment.MachineName);
        var configuration = _configuration;
        var peerAdministration = new PeerAdministrationService(configuration, _trust, _permissions, PeerSessions);
        RemoteMountManager? remoteMounts = OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
            ? new RemoteMountManager(configuration, PeerSessions, log: message => Console.Error.WriteLine(message))
            : null;
        await using var web = new DaemonWebHost(PeerSessions, _trust, _permissions, configuration,
            peerAdministration, pairing, remoteMounts, _webPort);
        var localIpc = new LocalIpcServer(PeerSessions, pairing, configuration, _trust, _permissions,
            _dispatcher.Clipboard, peerAdministration, remoteMounts, () => $"http://127.0.0.1:{web.Port}/");
        using var daemonStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listener.Start();
        Task kvmCoordinator = Task.CompletedTask;
        Task remoteMountTask = Task.CompletedTask;
        Task localIpcTask = Task.CompletedTask;
        try
        {
            await pairing.StartAsync(cancellationToken);
            try { await PeerSessions.StartAsync(cancellationToken); }
            catch (SocketException ex) { Console.Error.WriteLine($"LAN discovery unavailable: {ex.Message}"); }
            await web.StartAsync(cancellationToken);
            localIpcTask = localIpc.RunAsync(daemonStop.Token);
            if (OperatingSystem.IsWindows() && _enableNativeOrchestration)
            {
                kvmCoordinator = WindowsKvmCoordinator.RunAsync(configuration, PeerSessions, daemonStop.Token,
                    KvmDiagnosticLog.Write);
            }
            if (remoteMounts is not null && _enableNativeOrchestration)
                remoteMountTask = remoteMounts.RunAsync(daemonStop.Token);
            Console.WriteLine($"xas daemon listening on TCP {_port}; device {_identity.DeviceId}");
            var clients = new HashSet<Task>();
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient socket;
                try { socket = await listener.AcceptTcpClientAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                var task = PeerSessions.HandleClientAsync(socket, cancellationToken);
                clients.Add(task);
                clients.RemoveWhere(t => t.IsCompleted);
            }
            await Task.WhenAll(clients);
        }
        finally
        {
            listener.Stop();
            daemonStop.Cancel();
            try { await kvmCoordinator.ConfigureAwait(false); }
            catch (OperationCanceledException) when (daemonStop.IsCancellationRequested) { }
            try { await remoteMountTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (daemonStop.IsCancellationRequested) { }
            if (remoteMounts is not null) await remoteMounts.DisposeAsync().ConfigureAwait(false);
            try { await localIpcTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (daemonStop.IsCancellationRequested) { }
            await _input.DisposeAsync();
        }
    }

}
