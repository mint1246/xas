namespace Xas.Core;

/// <summary>A local user-session text clipboard. Implementations report unsupported when unavailable.</summary>
public interface ITextClipboardBackend
{
    bool IsAvailable { get; }
    ValueTask<ClipboardTextSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    ValueTask SetTextAsync(string text, CancellationToken cancellationToken);
}

/// <summary>Native clipboard change sequence and the current plain-text value.</summary>
public sealed record ClipboardTextSnapshot(string Text, ulong ChangeId);

/// <summary>One versioned plain-text clipboard value sent over the authenticated protocol.</summary>
public sealed record ClipboardPayload(string Origin, ulong Version, string Text);
