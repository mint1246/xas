using Xas.Core;

namespace Xas.Daemon.Input;

/// <summary>Explicit backend placeholder used where native input injection is unsupported.</summary>
public sealed class UnavailableInputBackend : IInputInjectionBackend
{
    public bool IsAvailable => false;

    public ValueTask InjectAsync(InputEvent inputEvent, CancellationToken cancellationToken) =>
        ValueTask.FromException(new PlatformNotSupportedException("Input injection is unavailable on this platform/session."));

    public ValueTask ReleaseAllAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
