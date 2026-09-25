using Xas.Cli.Clipboard;
using Xas.Core;

namespace Xas.Tests;

public static class ClipboardSyncTests
{
    public static Task RunAsync()
    {
        var initialLocal = new ClipboardTextSnapshot("local", 1);
        var initialRemote = new ClipboardPayload("remote", 1, "remote");
        var state = new ClipboardSyncState(initialLocal, initialRemote);
        Equal(ClipboardSyncAction.None, state.Decide(initialLocal, initialRemote, "a", "b"));
        Equal(ClipboardSyncAction.Push, state.Decide(new("new local", 2), initialRemote, "a", "b"));
        Equal(ClipboardSyncAction.Pull, state.Decide(initialLocal, new("remote", 2, "new remote"), "a", "b"));
        Equal(ClipboardSyncAction.Pull, state.Decide(new("both local", 2),
            new("remote", 2, "both remote"), "a", "b"));
        Equal(ClipboardSyncAction.Push, state.Decide(new("both local", 2),
            new("remote", 2, "both remote"), "z", "b"));
        Equal(ClipboardSyncAction.None, state.Decide(new("same", 2),
            new("remote", 2, "same"), "a", "b"));
        state.Observe(new("same", 2), new("a", 2, "same"));
        Equal(ClipboardSyncAction.None, state.Decide(new("same", 2),
            new("a", 3, "previous local edit"), "a", "b"));
        return Task.CompletedTask;
    }

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
}
