using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Security;

namespace Xas.Cli;

internal static class LocalSetupCommands
{
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0) return null;
        switch (args[0])
        {
            case "identity":
                if (args.Length != 1) return Usage("Usage: xas identity");
                using (var identity = DeviceIdentity.LoadOrCreate(AppPaths.IdentityDirectory, Environment.MachineName))
                {
                    Console.WriteLine($"Device ID: {identity.DeviceId}");
                    Console.WriteLine($"Fingerprint: {PairingFingerprint.Format(identity.Fingerprint)}");
                }
                return 0;
            case "pair":
                if (args.Length is < 4 or > 6 || !int.TryParse(args.ElementAtOrDefault(4) ?? XasProtocol.DefaultPort.ToString(), out var port)
                    || port is < 1 or > 65535)
                    return Usage("Usage: xas pair <device-id> <fingerprint> <host> [port] [name]");
                using (var local = DeviceIdentity.LoadOrCreate(AppPaths.IdentityDirectory, Environment.MachineName))
                {
                    var id = args[1];
                    var fingerprint = args[2];
                    var code = PairingFingerprint.ConfirmationCode(local.Fingerprint, fingerprint);
                    Console.WriteLine($"Verification code: {code}");
                    Console.WriteLine("Compare this code on both machines before approving the peer.");
                    Console.Write("Approve this peer locally? Type YES: ");
                    if (!string.Equals(await Console.In.ReadLineAsync(), "YES", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine("Pairing not approved.");
                        return 1;
                    }
                    var name = args.ElementAtOrDefault(5) ?? id;
                    var trust = new PeerTrustStore(AppPaths.TrustDirectory);
                    trust.Approve(id, fingerprint, name);
                    new LocalConfiguration().UpsertPeer(new ConfiguredPeer(id, name, args[3], port));
                    Console.WriteLine($"Paired {id}. Grant individual permissions with 'xas allow {id} shell'.");
                }
                return 0;
            case "allow":
            case "deny":
                if (args.Length != 3 || !Enum.TryParse<Capability>(args[2], true, out var capability))
                    return Usage($"Usage: xas {args[0]} <device-id> <shell|filesystem|input|clipboard|privilegedshell>");
                var peer = new LocalConfiguration().Resolve(args[1]);
                if (peer is null || !new PeerTrustStore(AppPaths.TrustDirectory).List().Any(p => p.DeviceId == peer.DeviceId))
                    throw new InvalidOperationException("Peer is not locally paired.");
                new PeerPermissionStore(AppPaths.TrustDirectory).SetAllowed(peer.DeviceId, capability, args[0] == "allow");
                Console.WriteLine($"{capability} {(args[0] == "allow" ? "allowed" : "denied")} for {peer.DeviceId}.");
                return 0;
            case "peers":
                foreach (var p in new PeerTrustStore(AppPaths.TrustDirectory).List())
                    Console.WriteLine($"{p.DeviceId}\t{p.DisplayName}\t{p.ApprovedAtUtc:O}");
                return 0;
            case "display":
                return DisplayCommands.Run(args);
            case "revoke":
                if (args.Length != 2) return Usage("Usage: xas revoke <device-id>");
                var config = new LocalConfiguration();
                var revoked = config.Resolve(args[1]) ?? throw new InvalidOperationException("No matching paired device.");
                var removed = new PeerTrustStore(AppPaths.TrustDirectory).Revoke(revoked.DeviceId);
                new PeerPermissionStore(AppPaths.TrustDirectory).RemovePeer(revoked.DeviceId);
                config.RemovePeer(revoked.DeviceId);
                Console.WriteLine(removed ? $"Revoked {revoked.DeviceId}." : $"Removed local configuration for {revoked.DeviceId}.");
                return 0;
            case "endpoint":
                if (args.Length is < 3 or > 4 || !int.TryParse(args.ElementAtOrDefault(3) ?? XasProtocol.DefaultPort.ToString(), out var endpointPort)
                    || endpointPort is < 1 or > 65535)
                    return Usage("Usage: xas endpoint <device-id> <host> [port]");
                var localConfig = new LocalConfiguration();
                var configured = localConfig.Resolve(args[1]) ?? throw new InvalidOperationException("No matching paired device.");
                localConfig.UpsertPeer(configured with { Host = args[2], Port = endpointPort });
                Console.WriteLine($"Endpoint for {configured.DeviceId}: {args[2]}:{endpointPort}");
                return 0;
            default:
                return null;
        }
    }

    private static int Usage(string message) { Console.Error.WriteLine(message); return 2; }
}
