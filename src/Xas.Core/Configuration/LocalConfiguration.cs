using System.Text.Json;

namespace Xas.Core.Configuration;

public static class AppPaths
{
    public static string Root
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("XAS_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xas");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return Path.Combine(string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg, "xas");
        }
    }

    public static string IdentityDirectory => Path.Combine(Root, "identity");
    public static string TrustDirectory => Path.Combine(Root, "trust");
}

public sealed record ConfiguredPeer(string DeviceId, string Name, string Host, int Port);

/// <summary>Per-user nonsecret peer endpoints and default selection.</summary>
public sealed class LocalConfiguration
{
    private readonly string _path;
    private readonly object _gate = new();
    private Settings _settings;

    public LocalConfiguration(string? root = null)
    {
        var directory = root ?? AppPaths.Root;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _settings = File.Exists(_path)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(_path)) ?? throw new InvalidDataException("Invalid settings file.")
            : new Settings(null, []);
    }

    public string? DefaultDeviceId { get { lock (_gate) return _settings.DefaultDeviceId; } }
    public IReadOnlyList<ConfiguredPeer> Peers { get { lock (_gate) return _settings.Peers.ToArray(); } }

    public ConfiguredPeer? Resolve(string? idPrefix)
    {
        lock (_gate)
        {
            var id = idPrefix ?? _settings.DefaultDeviceId;
            if (string.IsNullOrWhiteSpace(id)) return null;
            var matches = _settings.Peers.Where(p => p.DeviceId.StartsWith(id, StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException($"Ambiguous device ID: {id}");
            return matches.SingleOrDefault();
        }
    }

    public void UpsertPeer(ConfiguredPeer peer)
    {
        if (peer.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(peer));
        lock (_gate)
        {
            var peers = _settings.Peers.Where(p => p.DeviceId != peer.DeviceId).Append(peer).ToArray();
            _settings = _settings with { Peers = peers };
            Save();
        }
    }

    public void SetDefault(string idPrefix)
    {
        var peer = Resolve(idPrefix) ?? throw new InvalidOperationException("No matching paired device.");
        lock (_gate) { _settings = _settings with { DefaultDeviceId = peer.DeviceId }; Save(); }
    }

    public bool RemovePeer(string deviceId)
    {
        lock (_gate)
        {
            var peers = _settings.Peers.Where(p => p.DeviceId != deviceId).ToArray();
            if (peers.Length == _settings.Peers.Length) return false;
            _settings = _settings with
            {
                Peers = peers,
                DefaultDeviceId = _settings.DefaultDeviceId == deviceId ? null : _settings.DefaultDeviceId
            };
            Save();
            return true;
        }
    }

    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_settings));
        File.Move(temp, _path, true);
    }

    private sealed record Settings(string? DefaultDeviceId, ConfiguredPeer[] Peers);
}
