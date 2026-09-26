using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Security;

namespace Xas.Daemon;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        LinuxSessionEnvironment.Ensure();
        if (args.Length is 0 || args[0] is "--help" or "help")
        {
            Console.WriteLine("Usage: Xas.Daemon serve [--port <1-65535>]");
            return 0;
        }
        if (args[0] != "serve") { Console.Error.WriteLine("Unknown daemon command."); return 2; }
        var port = XasProtocol.DefaultPort;
        if (args.Length == 3 && args[1] == "--port" && int.TryParse(args[2], out var parsed) && parsed is >= 1 and <= 65535)
            port = parsed;
        else if (args.Length != 1) { Console.Error.WriteLine("Invalid port."); return 2; }

        using var identity = DeviceIdentity.LoadOrCreate(AppPaths.IdentityDirectory, Environment.MachineName);
        var trust = new PeerTrustStore(AppPaths.TrustDirectory);
        var permissions = new PeerPermissionStore(AppPaths.TrustDirectory);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        try { await new DaemonHost(identity, trust, permissions, port).RunAsync(shutdown.Token); return 0; }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
