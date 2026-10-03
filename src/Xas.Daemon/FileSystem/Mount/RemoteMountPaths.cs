using System.Security.Cryptography;
using System.Text;
using Xas.Core.FileSystem;

namespace Xas.Daemon.FileSystem.Mount;

/// <summary>Reserves readable mount names for the daemon lifetime, including across reconnects.</summary>
internal sealed class RemoteMountPaths(string root)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Parent, string Id), string> _paths = new();
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);

    public string GetMountPoint(string deviceId, string deviceName, RemoteVolume volume)
    {
        lock (_gate)
        {
            var device = Reserve(root, deviceId, deviceName);
            return Reserve(device, volume.Id, volume.Name);
        }
    }

    private string Reserve(string parent, string id, string name)
    {
        var key = (parent, id);
        if (_paths.TryGetValue(key, out var existing)) return existing;
        var readable = SafePathComponent(name);
        var path = Path.Combine(parent, readable);
        if (!_reserved.Add(path))
        {
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))
                .ToLowerInvariant()[..8];
            var suffix = 0;
            do
            {
                path = Path.Combine(parent, suffix == 0 ? $"{readable}-{digest}" : $"{readable}-{digest}-{suffix}");
                suffix++;
            } while (!_reserved.Add(path));
        }
        _paths[key] = path;
        return path;
    }

    private static string SafePathComponent(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        // Windows drive roots such as F:\ should appear as F: on Linux.
        var chars = name.Trim().TrimEnd('/', '\\')
            .Select(ch => char.IsControl(ch) || invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch).ToArray();
        var readable = new string(chars).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(readable)) readable = "remote";
        return readable.Length > 48 ? readable[..48] : readable;
    }
}
