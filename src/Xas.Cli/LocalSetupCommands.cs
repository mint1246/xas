using System.Diagnostics;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.LocalIpc;
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
                return RunIdentity(args);
            case "ui":
                return await RunUiAsync(args);
            case "pair":
                return await RunPairAsync(args.Skip(1).ToArray());
            case "pair-manual":
                return await RunManualPairingAsync(args);
            case "allow":
            case "deny":
                return SetPeerPermission(args);
            case "peers":
                foreach (var p in new PeerTrustStore(AppPaths.TrustDirectory).List())
                    Console.WriteLine($"{p.DeviceId}\t{p.DisplayName}\t{p.ApprovedAtUtc:O}");
                return 0;
            case "display":
                return DisplayCommands.Run(args);
            case "revoke":
                if (args.Length != 2) return Usage("Usage: xas revoke <device-id>");
                using (var revokeClient = CreateClient())
                {
                    var revoked = await revokeClient.RevokeAsync(args[1], CancellationToken.None);
                    Console.WriteLine($"Revoked {revoked} and disconnected all live sessions.");
                }
                return 0;
            case "endpoint":
                return await UpdateEndpointAsync(args);
            default:
                return null;
        }
    }

    private static LocalDaemonClient CreateClient() => new(
        Stream.Null,
        Console.OpenStandardOutput(),
        Console.OpenStandardError());

    private static int RunIdentity(string[] args)
    {
        if (args.Length != 1) return Usage("Usage: xas identity");
        using var identity = DeviceIdentity.LoadOrCreate(
            AppPaths.IdentityDirectory, Environment.MachineName);
        Console.WriteLine($"Device ID: {identity.DeviceId}");
        Console.WriteLine($"Fingerprint: {PairingFingerprint.Format(identity.Fingerprint)}");
        return 0;
    }

    private static async Task<int> RunUiAsync(string[] args)
    {
        if (args.Length != 1) return Usage("Usage: xas ui");
        using var uiClient = CreateClient();
        var url = await uiClient.GetUiUrlAsync(CancellationToken.None);
        Console.WriteLine(url);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return 0;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"Could not open the default browser: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunManualPairingAsync(string[] args)
    {
        var portText = args.ElementAtOrDefault(4) ?? XasProtocol.DefaultPort.ToString();
        if (args.Length is < 4 or > 5 || !int.TryParse(portText, out var port) || port is < 1 or >= 65535)
            return Usage("Usage: xas pair-manual <device-id> <fingerprint> <host> [control-port]");

        using var client = CreateClient();
        var pending = await client.BeginPairingAsync(args[3], port, args[1], CancellationToken.None);
        if (!FingerprintsMatch(args[2], pending.Fingerprint))
        {
            Console.Error.WriteLine("The device fingerprint does not match the supplied fingerprint.");
            await client.ApprovePairingAsync(pending.PairingId, false, CancellationToken.None);
            return 1;
        }
        return await ApprovePendingAsync(client, pending, PairPermissionPreset.Personal);
    }

    private static int SetPeerPermission(string[] args)
    {
        if (args.Length != 3 || !Enum.TryParse<Capability>(args[2], true, out var capability))
            return Usage(
                $"Usage: xas {args[0]} <device-id> <shell|filesystem|input|clipboard|privilegedshell>");
        if (capability == Capability.PrivilegedShell && !OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                "PrivilegedShell is Windows-only. On Linux, an allowed shell may use the " +
                "system sudo policy normally.");

        var peer = new LocalConfiguration().Resolve(args[1]);
        var approvedPeers = new PeerTrustStore(AppPaths.TrustDirectory).List();
        if (peer is null || !approvedPeers.Any(approved => approved.DeviceId == peer.DeviceId))
            throw new InvalidOperationException("Peer is not locally paired.");

        var allow = args[0] == "allow";
        new PeerPermissionStore(AppPaths.TrustDirectory)
            .SetAllowed(peer.DeviceId, capability, allow);
        var decision = allow ? "allowed" : "denied";
        Console.WriteLine($"{capability} {decision} for {peer.DeviceId}.");
        return 0;
    }

    private static async Task<int> UpdateEndpointAsync(string[] args)
    {
        if (args.Length is < 3 or > 4)
            return Usage("Usage: xas endpoint <device-id> <host> [port]");

        var portText = args.ElementAtOrDefault(3) ?? XasProtocol.DefaultPort.ToString();
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            return Usage("Usage: xas endpoint <device-id> <host> [port]");

        using var endpointClient = CreateClient();
        var updated = await endpointClient.UpdateEndpointAsync(
            args[1], args[2], port, CancellationToken.None);
        Console.WriteLine($"Endpoint for {updated}: {args[2]}:{port}");
        return 0;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static async Task<int> RunPairAsync(string[] args)
    {
        var preset = PairPermissionPreset.Personal;
        string? query = null;
        foreach (var arg in args)
        {
            if (arg == "--kvm")
            {
                preset = PairPermissionPreset.Kvm;
                continue;
            }
            if (arg == "--trust-only")
            {
                preset = PairPermissionPreset.None;
                continue;
            }
            if (arg.StartsWith("-", StringComparison.Ordinal) || query is not null)
                return Usage("Usage: xas pair [device-name|short-id] [--kvm|--trust-only]");
            query = arg;
        }

        using var client = CreateClient();
        var pending = await client.ListPairingsAsync(CancellationToken.None);
        var incoming = pending
            .Where(pairing => pairing.Incoming &&
                (query is null || MatchesPairTarget(
                    pairing.DeviceId, pairing.DisplayName, query)))
            .ToArray();
        var incomingResult = await HandleIncomingPairingsAsync(client, incoming, preset);
        if (incomingResult.HasValue) return incomingResult.Value;

        var candidates = await client.ListPairingCandidatesAsync(CancellationToken.None);
        var candidate = await SelectCandidateAsync(candidates, query);
        if (candidate is null) return 1;

        Console.WriteLine(
            $"Starting pairing with {candidate.DisplayName} " +
            $"({ShortId(candidate.DeviceId)})...");
        var started = await client.BeginDiscoveredPairingAsync(
            candidate.DeviceId, CancellationToken.None);
        return await ApprovePendingAsync(client, started, preset);
    }

    private static async Task<int?> HandleIncomingPairingsAsync(
        LocalDaemonClient client,
        LocalPairPending[] incoming,
        PairPermissionPreset preset)
    {
        if (incoming.Length == 0) return null;
        if (incoming.Length == 1)
            return await ApprovePendingAsync(client, incoming[0], preset);

        Console.WriteLine("Incoming pairing requests:");
        for (var index = 0; index < incoming.Length; index++)
        {
            var pairing = incoming[index];
            Console.WriteLine(
                $"  {index + 1}. {pairing.DisplayName}  " +
                $"{ShortId(pairing.DeviceId)}  code {FormatCode(pairing.Code)}");
        }

        var selectedIndex = await ReadSelectionAsync(incoming.Length);
        if (selectedIndex < 0) return 1;
        return await ApprovePendingAsync(client, incoming[selectedIndex], preset);
    }

    private static async Task<LocalPairCandidate?> SelectCandidateAsync(
        IReadOnlyList<LocalPairCandidate> candidates, string? query)
    {
        if (query is not null)
            return FindCandidate(candidates, query);
        if (candidates.Count == 0)
        {
            Console.WriteLine("No unpaired XAS devices are currently discovered on the LAN.");
            return null;
        }
        if (candidates.Count == 1) return candidates[0];

        Console.WriteLine("Nearby unpaired XAS devices:");
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            Console.WriteLine(
                $"  {index + 1}. {candidate.DisplayName}  " +
                $"{ShortId(candidate.DeviceId)}  {candidate.Address}");
        }

        var selectedIndex = await ReadSelectionAsync(candidates.Count);
        return selectedIndex < 0 ? null : candidates[selectedIndex];
    }

    private static LocalPairCandidate FindCandidate(
        IReadOnlyList<LocalPairCandidate> candidates, string query)
    {
        var matches = candidates
            .Where(candidate => MatchesPairTarget(
                candidate.DeviceId, candidate.DisplayName, query))
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException(
                $"No unpaired discovered device matches '{query}'.");
        if (matches.Length > 1)
            throw new InvalidOperationException($"Pairing target '{query}' is ambiguous.");
        return matches[0];
    }

    private static async Task<int> ApprovePendingAsync(LocalDaemonClient client, LocalPairPending pending,
        PairPermissionPreset preset)
    {
        Console.WriteLine($"Pairing request: {pending.DisplayName} ({ShortId(pending.DeviceId)})");
        Console.WriteLine($"Verification code: {FormatCode(pending.Code)}");
        Console.WriteLine("Confirm that the same code is shown on the other device.");
        var presetText = preset switch
        {
            PairPermissionPreset.Kvm => "KVM only",
            PairPermissionPreset.None => "trust only (no capabilities)",
            _ => "personal device (input, clipboard, files, shell)"
        };
        Console.Write($"Approve and trust as {presetText}? Type YES: ");
        if (!string.Equals(await Console.In.ReadLineAsync(), "YES", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Pairing not approved.");
            await client.ApprovePairingAsync(pending.PairingId, false, CancellationToken.None, preset);
            return 1;
        }
        Console.WriteLine($"Waiting for approval on {pending.DisplayName}...");
        await client.ApprovePairingAsync(pending.PairingId, true, CancellationToken.None, preset);
        Console.WriteLine(
            $"Paired {pending.DisplayName} ({ShortId(pending.DeviceId)}). " +
            $"Permissions: {presetText}.");
        return 0;
    }

    private static async Task<int> ReadSelectionAsync(int count)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                "Multiple devices are available; specify one with " +
                "'xas pair <name-or-short-id>'.");
            return -1;
        }
        Console.Write($"Select device [1-{count}]: ");
        var text = await Console.In.ReadLineAsync();
        return int.TryParse(text, out var value) && value >= 1 && value <= count ? value - 1 : -1;
    }

    private static bool MatchesPairTarget(string deviceId, string displayName, string query) =>
        string.Equals(deviceId, query, StringComparison.OrdinalIgnoreCase) ||
        deviceId.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(displayName, query, StringComparison.OrdinalIgnoreCase);

    private static string ShortId(string deviceId) => deviceId.Length > 12 ? deviceId[..12] : deviceId;
    private static string FormatCode(string code) => code.Length == 6 ? code[..3] + " " + code[3..] : code;

    private static bool FingerprintsMatch(string left, string right)
    {
        static string Normalize(string value)
        {
            var hexCharacters = value.Where(Uri.IsHexDigit);
            var uppercaseCharacters = hexCharacters.Select(char.ToUpperInvariant);
            return new string(uppercaseCharacters.ToArray());
        }
        var a = Normalize(left);
        var b = Normalize(right);
        return a.Length == 64 && b.Length == 64 && string.Equals(a, b, StringComparison.Ordinal);
    }
}
