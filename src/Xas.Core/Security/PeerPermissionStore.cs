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
        _grants = ReadGrants();
    }

    public bool IsAllowed(string peerId, Capability capability)
    {
        lock (_gate)
        {
            Refresh();
            return _grants.TryGetValue(peerId, out var grants) && grants.Contains(capability);
        }
    }

    public IReadOnlySet<Capability> GetAllowed(string peerId)
    {
        lock (_gate)
        {
            Refresh();
            return _grants.TryGetValue(peerId, out var grants)
                ? grants.ToHashSet()
                : new HashSet<Capability>();
        }
    }

    public void SetAllowed(string peerId, Capability capability, bool allowed)
    {
        lock (_gate)
        {
            Refresh();
            if (!_grants.TryGetValue(peerId, out var grants))
                _grants[peerId] = grants = [];

            if (allowed)
                grants.Add(capability);
            else
                grants.Remove(capability);
            Save();
        }
    }

    public void RemovePeer(string peerId)
    {
        lock (_gate)
        {
            Refresh();
            if (_grants.Remove(peerId)) Save();
        }
    }

    private void Refresh() => _grants = ReadGrants();

    private Dictionary<string, HashSet<Capability>> ReadGrants()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, HashSet<Capability>>();

        var contents = File.ReadAllBytes(_path);
        return JsonSerializer.Deserialize<Dictionary<string, HashSet<Capability>>>(contents)
            ?? new Dictionary<string, HashSet<Capability>>();
    }

    private void Save()
    {
        var temp = _path + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (OperatingSystem.IsLinux())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(temp, options);
        JsonSerializer.Serialize(stream, _grants);
        stream.Flush(true);
        File.Move(temp, _path, true);
    }
}
