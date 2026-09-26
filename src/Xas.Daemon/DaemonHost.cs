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
using Xas.Input;

namespace Xas.Daemon;

public sealed class DaemonHost
{
    private readonly DeviceIdentity _identity;
    private readonly PeerTrustStore _trust;
    private readonly PeerPermissionStore _permissions;
    private readonly int _port;
    private readonly InputControlService _input;
    private readonly RequestDispatcher _dispatcher;
    public PeerSessionManager PeerSessions { get; }

    public DaemonHost(DeviceIdentity identity, PeerTrustStore trust, PeerPermissionStore permissions,
        int port = XasProtocol.DefaultPort, IInputInjectionBackend? inputBackend = null,
        ITextClipboardBackend? clipboardBackend = null, IInputPipelineMetrics? inputMetrics = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _port = port;
        _input = new InputControlService(permissions,
            inputBackend ?? (OperatingSystem.IsWindows() ? new WindowsSendInputBackend() :
                OperatingSystem.IsLinux() ? CreateLinuxInputBackend() : new UnavailableInputBackend()), inputMetrics);
        _dispatcher = new RequestDispatcher(identity, permissions, () => _input.ProtocolVersion, clipboardBackend);
        PeerSessions = new PeerSessionManager(identity, trust, permissions, _input, _dispatcher, port);
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
        await using var web = new DaemonWebHost(PeerSessions, _trust, _permissions, pairing);
        using var handoffStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listener.Start();
        Task handoff = Task.CompletedTask;
        try
        {
            await pairing.StartAsync(cancellationToken);
            try { await PeerSessions.StartAsync(cancellationToken); }
            catch (SocketException ex) { Console.Error.WriteLine($"LAN discovery unavailable: {ex.Message}"); }
            await web.StartAsync(cancellationToken);
            if (OperatingSystem.IsWindows() && new LocalConfiguration().Resolve(null) is { } configured &&
                PeerSessions.GetSession(configured.DeviceId) is { } hotPeer)
                handoff = WindowsMonitorHandoff.RunAsync(hotPeer, handoffStop.Token,
                    message => Console.Error.WriteLine(message));
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
            handoffStop.Cancel();
            try { await handoff.ConfigureAwait(false); }
            catch (OperationCanceledException) when (handoffStop.IsCancellationRequested) { }
            await _input.DisposeAsync();
        }
    }

}
