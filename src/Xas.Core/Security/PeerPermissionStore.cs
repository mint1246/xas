using System.Text.Json;

namespace Xas.Core.Security;

/// <summary>Local grants. No protocol method may modify this store.</summary>
public sealed class PeerPermissionStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, HashSet<Capability>> _grants;

    public PeerPermissionStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "permissions.json");
        _grants = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, HashSet<Capability>>>(File.ReadAllBytes(_path)) ?? new()
            : new();
    }

    public bool IsAllowed(string peerId, Capability capability)
    {
        lock (_gate) return _grants.TryGetValue(peerId, out var set) && set.Contains(capability);
    }

    public IReadOnlySet<Capability> GetAllowed(string peerId)
    {
        lock (_gate) return _grants.TryGetValue(peerId, out var set) ? set.ToHashSet() : new HashSet<Capability>();
    }

    public void SetAllowed(string peerId, Capability capability, bool allowed)
    {
        lock (_gate)
        {
            if (!_grants.TryGetValue(peerId, out var set)) _grants[peerId] = set = [];
            if (allowed) set.Add(capability); else set.Remove(capability);
            Save();
        }
    }

    private void Save()
    {
        var temp = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, _grants);
            stream.Flush(true);
        }
        File.Move(temp, _path, true);
    }
}
