using System.Net;
using System.Net.Sockets;
using Xas.Core;
using Xas.Core.Discovery;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Transport;
using Xas.Daemon.Interactive;
using Xas.Daemon.FileTransfer;
using Xas.Daemon.Input;

namespace Xas.Daemon;

public sealed class DaemonHost(DeviceIdentity identity, PeerTrustStore trust,
    PeerPermissionStore permissions, int port = XasProtocol.DefaultPort,
    IInputInjectionBackend? inputBackend = null, ITextClipboardBackend? clipboardBackend = null)
{
    private readonly InputControlService _input = new(permissions,
        inputBackend ?? (OperatingSystem.IsWindows() ? new WindowsSendInputBackend() :
            OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XAS_ENABLE_X11_INPUT") == "1"
                ? new LinuxX11InputBackend() : new UnavailableInputBackend()));
    private RequestDispatcher? _dispatcher;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _dispatcher = new RequestDispatcher(identity, permissions, () => _input.IsAvailable, clipboardBackend);
        var listener = new TcpListener(IPAddress.Any, port);
        await using var discovery = new LanDiscoveryService(identity.DeviceId, Environment.MachineName, port);
        listener.Start();
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
        finally { listener.Stop(); await _input.DisposeAsync(); }
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
