namespace Xas.Core;

/// <summary>A real platform pseudoterminal session. Output contains VT/UTF-8 terminal data.</summary>
public interface IInteractiveShellSession : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    ValueTask ResizeAsync(short columns, short rows, CancellationToken cancellationToken);
}

public interface IInteractiveShellBackend
{
    bool IsAvailable { get; }
    Task<IInteractiveShellSession> StartAsync(bool elevated, short columns, short rows,
        CancellationToken cancellationToken);
}
