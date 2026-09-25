namespace Xas.Core;

/// <summary>Features are advertised by implementations, then separately authorized per peer.</summary>
public enum Capability { Shell = 1, FileSystem = 2, Input = 3, Clipboard = 4, PrivilegedShell = 5, Display = 6 }

public sealed record CapabilityVersion(Capability Capability, ushort Version);

public sealed record DeviceInfo(string DeviceId, string Name, string Os, string Architecture,
    IReadOnlyList<CapabilityVersion> Capabilities);

public sealed record PeerGrant(string DeviceId, IReadOnlySet<Capability> AllowedCapabilities);

public sealed record ShellRequest(ShellMode Mode, string? Command, string? Executable,
    IReadOnlyList<string> Arguments, bool Elevated = false, string? WorkingDirectory = null);

public enum ShellMode { Interactive, Command, Exec }

public interface IShellBackend
{
    bool SupportsInteractive { get; }
    Task<int> RunAsync(ShellRequest request, Stream stdin, Stream stdout, Stream stderr,
        CancellationToken cancellationToken);
}

public interface IInputInjectionBackend
{
    bool IsAvailable { get; }
    ValueTask InjectAsync(InputEvent inputEvent, CancellationToken cancellationToken);
    ValueTask ReleaseAllAsync(CancellationToken cancellationToken);
}

/// <summary>Optional session preparation, such as desktop-portal consent, before capture begins.</summary>
public interface IInputActivationBackend
{
    ValueTask ActivateAsync(CancellationToken cancellationToken);
}

/// <summary>Backend that can position the remote cursor in display pixel coordinates.</summary>
public interface IAbsoluteInputInjectionBackend : IInputInjectionBackend { }

public interface IRemoteFilesystemBackend
{
    bool IsAvailable { get; }
}

public interface IClipboardBackend
{
    bool IsAvailable { get; }
}

public interface IVirtualDisplayController
{
    bool IsAvailable { get; }
}

public interface IPrivilegeBroker
{
    bool IsAvailable { get; }
}
