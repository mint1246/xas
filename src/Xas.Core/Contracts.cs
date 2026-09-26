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

/// <summary>A Windows monitor created to represent the paired Linux display, and the region it occupies.</summary>
public sealed record VirtualDisplayAttachment(string DeviceName, string MonitorHardwareId, Guid MonitorId,
    int WidthPixels, int HeightPixels, int RefreshHertz);

/// <summary>
/// Owns the Windows monitor that input handoff is bound to. Implementations attach the monitor on demand,
/// keep it alive for the lifetime of the process, and reattach it if Windows drops it. A monitor boundary
/// only exists while an attachment is held, so callers should attach once at startup and dispose on exit,
/// which releases the monitor.
/// </summary>
public interface IVirtualDisplayController : IAsyncDisposable
{
    /// <summary>True when a supported virtual display driver is installed and its control channel opens.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Creates, or recreates at a new mode, the monitor that mirrors <paramref name="display"/>. The
    /// returned <see cref="VirtualDisplayAttachment.DeviceName"/> is the GDI device name that
    /// <c>EnumDisplayMonitors</c> reports for the new monitor, such as <c>\\.\DISPLAY2</c>.
    /// </summary>
    ValueTask<VirtualDisplayAttachment> AttachAsync(DisplayMetadata display, CancellationToken cancellationToken);

    /// <summary>Removes the attached monitor. Safe to call when nothing is attached.</summary>
    ValueTask DetachAsync(CancellationToken cancellationToken);
}

public interface IPrivilegeBroker
{
    bool IsAvailable { get; }
}
