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
public sealed record FileSystemExport(string Id, string Path, string Name, bool ReadOnly);

/// <summary>Per-user nonsecret peer endpoints and default selection.</summary>
public sealed class LocalConfiguration
{
    private readonly string _path;
    private readonly object _gate = new();
    private Settings _settings;
    public event Action? Changed;

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
    public bool AutoExposeRemovable { get { lock (_gate) return _settings.AutoExposeRemovable; } }
    public bool AutoMountRemoteRemovable { get { lock (_gate) return _settings.AutoMountRemoteRemovable; } }
    public bool AutoExposeMainDrive { get { lock (_gate) return _settings.AutoExposeMainDrive; } }
    public bool AutoMountRemoteMainDrive { get { lock (_gate) return _settings.AutoMountRemoteMainDrive; } }
    public IReadOnlyList<FileSystemExport> FileSystemExports { get { lock (_gate) return _settings.FileSystemExports.ToArray(); } }

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
            _settings.Peers = peers;
            Save();
        }
        Changed?.Invoke();
    }

    public void SetDefault(string idPrefix)
    {
        var peer = Resolve(idPrefix) ?? throw new InvalidOperationException("No matching paired device.");
        lock (_gate) { _settings.DefaultDeviceId = peer.DeviceId; Save(); }
        Changed?.Invoke();
    }

    public bool RemovePeer(string deviceId)
    {
        var removed = false;
        lock (_gate)
        {
            var peers = _settings.Peers.Where(p => p.DeviceId != deviceId).ToArray();
            if (peers.Length == _settings.Peers.Length) return false;
            _settings.Peers = peers;
            if (_settings.DefaultDeviceId == deviceId) _settings.DefaultDeviceId = null;
            Save();
            removed = true;
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void SetAutoExposeRemovable(bool enabled)
    {
        lock (_gate) { _settings.AutoExposeRemovable = enabled; Save(); }
        Changed?.Invoke();
    }

    public void SetAutoMountRemoteRemovable(bool enabled)
    {
        lock (_gate) { _settings.AutoMountRemoteRemovable = enabled; Save(); }
        Changed?.Invoke();
    }

    public void SetAutoExposeMainDrive(bool enabled)
    {
        lock (_gate) { _settings.AutoExposeMainDrive = enabled; Save(); }
        Changed?.Invoke();
    }

    public void SetAutoMountRemoteMainDrive(bool enabled)
    {
        lock (_gate) { _settings.AutoMountRemoteMainDrive = enabled; Save(); }
        Changed?.Invoke();
    }

    public void UpsertFileSystemExport(FileSystemExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        if (string.IsNullOrWhiteSpace(export.Id) || export.Id.Length > 128 || export.Id.Any(char.IsControl))
            throw new ArgumentException("Filesystem export ID is invalid.", nameof(export));
        if (string.IsNullOrWhiteSpace(export.Path) || !Path.IsPathRooted(export.Path))
            throw new ArgumentException("Filesystem export path must be an absolute local path.", nameof(export));
        if (string.IsNullOrWhiteSpace(export.Name) || export.Name.Length > 128 || export.Name.Any(char.IsControl))
            throw new ArgumentException("Filesystem export name is invalid.", nameof(export));
        var normalized = Path.GetFullPath(export.Path);
        lock (_gate)
        {
            _settings.FileSystemExports = _settings.FileSystemExports
                .Where(item => !string.Equals(item.Id, export.Id, StringComparison.Ordinal))
                .Append(export with { Path = normalized }).ToArray();
            Save();
        }
        Changed?.Invoke();
    }

    public bool RemoveFileSystemExport(string id)
    {
        var removed = false;
        lock (_gate)
        {
            var exports = _settings.FileSystemExports.Where(item => !string.Equals(item.Id, id, StringComparison.Ordinal)).ToArray();
            if (exports.Length == _settings.FileSystemExports.Length) return false;
            _settings.FileSystemExports = exports;
            Save();
            removed = true;
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_settings));
        File.Move(temp, _path, true);
    }

    private sealed class Settings
    {
        public string? DefaultDeviceId { get; set; }
        public ConfiguredPeer[] Peers { get; set; } = [];
        public bool AutoExposeRemovable { get; set; } = true;
        public bool AutoMountRemoteRemovable { get; set; } = true;
        public bool AutoExposeMainDrive { get; set; } = true;
        public bool AutoMountRemoteMainDrive { get; set; } = true;
        public FileSystemExport[] FileSystemExports { get; set; } = [];

        public Settings() { }
        public Settings(string? defaultDeviceId, ConfiguredPeer[] peers)
        { DefaultDeviceId = defaultDeviceId; Peers = peers; }
    }
}
