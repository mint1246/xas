using Xas.Core;
using Xas.Core.LocalIpc;
using Xas.Core.Security;

namespace Xas.Daemon.Pairing;

internal static class PairPermissionPresets
{
    public static void Apply(PeerPermissionStore permissions, string deviceId, PairPermissionPreset preset)
    {
        var capabilities = new[]
        {
            Capability.Input,
            Capability.Clipboard,
            Capability.FileSystem,
            Capability.Shell
        };
        foreach (var capability in capabilities)
        {
            permissions.SetAllowed(deviceId, capability, preset switch
            {
                PairPermissionPreset.None => false,
                PairPermissionPreset.Kvm => capability == Capability.Input,
                PairPermissionPreset.Personal => true,
                _ => throw new InvalidDataException("Unknown pairing permission preset.")
            });
        }

        // Administrator brokering is always a separate, explicit Windows-only grant.
        permissions.SetAllowed(deviceId, Capability.PrivilegedShell, false);
    }
}
