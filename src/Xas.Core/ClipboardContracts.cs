namespace Xas.Core;

/// <summary>A local user-session text clipboard. Implementations report unsupported when unavailable.</summary>
public interface ITextClipboardBackend
{
    bool IsAvailable { get; }
    ValueTask<ClipboardTextSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    ValueTask SetTextAsync(string text, CancellationToken cancellationToken);
}

/// <summary>Optional native notification source. Changes are sampled only on local OS notifications.</summary>
public interface IClipboardChangeSource
{
    IAsyncEnumerable<ClipboardTextSnapshot> WatchChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Native clipboard change sequence and the current plain-text value.</summary>
public sealed record ClipboardTextSnapshot(string Text, ulong ChangeId);

/// <summary>One versioned plain-text clipboard value sent over the authenticated protocol.</summary>
public sealed record ClipboardPayload(string Origin, ulong Version, string Text);
