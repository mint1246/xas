using Xas.Daemon.Web;

namespace Xas.Tests;

public static class WebSessionTests
{
    public static Task RunAsync()
    {
        var store = new WebSessionStore();
        var now = DateTimeOffset.UtcNow;
        var tabOneToken = store.GetOrCreate("browser-session", now);
        var reloadedTabToken = store.GetOrCreate("browser-session", now.AddMinutes(1));
        if (tabOneToken != reloadedTabToken)
            throw new InvalidOperationException("Reloading the page changed the shared browser CSRF token.");
        if (!store.IsValid("browser-session", tabOneToken, now.AddMinutes(2)))
            throw new InvalidOperationException("A second tab's token became invalid after reload.");

        var otherBrowserToken = store.GetOrCreate("other-browser", now);
        if (!store.IsValid("browser-session", tabOneToken, now.AddMinutes(3)) ||
            !store.IsValid("other-browser", otherBrowserToken, now.AddMinutes(3)))
            throw new InvalidOperationException("Creating another browser session invalidated an existing session.");
        if (store.IsValid("browser-session", tabOneToken, now.AddMinutes(32)))
            throw new InvalidOperationException("An expired session remained authorized.");
        if (store.IsValid("other-browser", otherBrowserToken, now.AddMinutes(31)))
            throw new InvalidOperationException("An expired session remained authorized.");

        var bounded = new WebSessionStore();
        var oldestToken = bounded.GetOrCreate("oldest", now);
        for (var i = 0; i < 255; i++) bounded.GetOrCreate("bounded-" + i, now.AddMinutes(1));
        bounded.GetOrCreate("last", now.AddMinutes(1));
        if (bounded.IsValid("oldest", oldestToken, now.AddMinutes(1)))
            throw new InvalidOperationException("The session store did not evict an old entry at its bound.");
        return Task.CompletedTask;
    }
}
