using System.Net;
using System.Net.Sockets;
using Xas.Core;
using Xas.Core.Discovery;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Transport;

namespace Xas.Daemon;

public sealed class DaemonHost(DeviceIdentity identity, PeerTrustStore trust,
    PeerPermissionStore permissions, int port = XasProtocol.DefaultPort)
{
    private readonly RequestDispatcher _dispatcher = new(identity, permissions);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
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
        finally { listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient socket, CancellationToken cancellationToken)
    {
        using var shutdownRegistration = cancellationToken.Register(static state => ((TcpClient)state!).Dispose(), socket);
        try
        {
            await using var tls = await MutualTlsTransport.AcceptAsync(socket, identity, trust,
                TimeSpan.FromSeconds(10), cancellationToken);
            await using var frames = new BinaryFrameConnection(tls.Stream, leaveOpen: true);
            await using var peer = new MultiplexedProtocolPeer(frames,
                (message, ct) => _dispatcher.HandleAsync(tls.PeerDeviceId, message, frames.SendAsync, ct));
            await peer.Completion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"Peer connection ended: {ex.Message}"); }
    }
}
