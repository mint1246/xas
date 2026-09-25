using Xas.Core;

namespace Xas.Cli.Clipboard;

public enum ClipboardSyncAction { None, Push, Pull }

/// <summary>Tracks two observed clipboard versions and resolves concurrent edits consistently.</summary>
public sealed class ClipboardSyncState(ClipboardTextSnapshot local, ClipboardPayload remote)
{
    private ClipboardTextSnapshot _local = local;
    private ClipboardPayload _remote = remote;

    public ClipboardSyncAction Decide(ClipboardTextSnapshot local, ClipboardPayload remote,
        string localDeviceId, string remoteDeviceId)
    {
        if (local.Text == remote.Text) return ClipboardSyncAction.None;
        var localChanged = local.ChangeId != _local.ChangeId || local.Text != _local.Text;
        var remoteChanged = remote.Origin != _remote.Origin || remote.Version != _remote.Version ||
            remote.Text != _remote.Text;
        if (!localChanged && !remoteChanged) return ClipboardSyncAction.None;
        if (localChanged && !remoteChanged) return ClipboardSyncAction.Push;
        if (!localChanged && remoteChanged)
            return remote.Origin == localDeviceId ? ClipboardSyncAction.None : ClipboardSyncAction.Pull;
        if (remote.Origin == localDeviceId) return ClipboardSyncAction.Push;
        return string.CompareOrdinal(localDeviceId, remoteDeviceId) > 0
            ? ClipboardSyncAction.Push : ClipboardSyncAction.Pull;
    }

    public void Observe(ClipboardTextSnapshot local, ClipboardPayload remote)
    { _local = local; _remote = remote; }
}
