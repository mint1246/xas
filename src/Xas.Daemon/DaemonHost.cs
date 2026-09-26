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
using Xas.Input;

namespace Xas.Daemon;

public sealed class DaemonHost(DeviceIdentity identity, PeerTrustStore trust,
    PeerPermissionStore permissions, int port = XasProtocol.DefaultPort,
    IInputInjectionBackend? inputBackend = null, ITextClipboardBackend? clipboardBackend = null)
{
    private readonly InputControlService _input = new(permissions,
        inputBackend ?? (OperatingSystem.IsWindows() ? new WindowsSendInputBackend() :
            OperatingSystem.IsLinux() ? CreateLinuxInputBackend() : new UnavailableInputBackend()));
    private RequestDispatcher? _dispatcher;

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
        _dispatcher = new RequestDispatcher(identity, permissions, () => _input.ProtocolVersion, clipboardBackend);
        var listener = new TcpListener(IPAddress.Any, port);
        await using var discovery = new LanDiscoveryService(identity.DeviceId, Environment.MachineName, port);
        using var handoffStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listener.Start();
        var handoff = OperatingSystem.IsWindows()
            ? WindowsMonitorHandoff.RunAsync(() => new LocalConfiguration().Resolve(null),
                identity, trust, Console.Error, handoffStop.Token)
            : Task.CompletedTask;
        try
        {
            try { await discovery.StartAsync(cancellationToken); }
            catch (SocketException ex) { Console.Error.WriteLine($"LAN discovery unavailable: {ex.Message}"); }
            Console.WriteLine($"xas daemon listening on TCP {port}; device {identity.DeviceId}");
            var clients = new HashSet<Task>();
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient socket;
                try { socket = await listener.AcceptTcpClientAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                var task = HandleClientAsync(socket, cancellationToken);
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

    private async Task HandleClientAsync(TcpClient socket, CancellationToken cancellationToken)
    {
        using var shutdownRegistration = cancellationToken.Register(static state => ((TcpClient)state!).Dispose(), socket);
        try
        {
            await using var tls = await MutualTlsTransport.AcceptAsync(socket, identity, trust,
                TimeSpan.FromSeconds(10), cancellationToken);
            await using var frames = new BinaryFrameConnection(tls.Stream, leaveOpen: true);
            await using var interactive = new InteractiveShellManager(tls.PeerDeviceId, permissions, frames.SendAsync);
            await using var files = new FileTransferServer(tls.PeerDeviceId, permissions, frames.SendAsync);
            await using var input = _input.CreateSession(tls.PeerDeviceId);
            await using var peer = new MultiplexedProtocolPeer(frames,
                (message, ct) => message.Method is "shell.open" or "shell.resize" or "shell.close"
                    ? interactive.HandleRequestAsync(message, ct)
                    : message.Method.StartsWith("file.", StringComparison.Ordinal)
                    ? files.HandleRequestAsync(message, ct)
                    : message.Method is "input.open" or "input.close"
                    ? input.HandleRequestAsync(message, ct)
                    : _dispatcher!.HandleAsync(tls.PeerDeviceId, message, frames.SendAsync, ct));
            peer.MessageReceived += message => message.Method switch
            {
                "shell.input" => interactive.HandleMessageAsync(message),
                "file.put.data" => files.HandleMessageAsync(message),
                "input.event" => input.HandleMessageAsync(message),
                _ => ValueTask.FromException(new InvalidDataException($"Unexpected stream message: {message.Method}"))
            };
            await peer.Completion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"Peer connection ended: {ex.Message}"); }
    }
}
