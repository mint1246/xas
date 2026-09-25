using System.Text;
using Xas.Daemon.Shell;

namespace Xas.Tests;

public static class ConPtyTests
{
    public static async Task RunAsync()
    {
        var backend = new WindowsConPtyBackend();
        try
        {
            await backend.StartAsync(elevated: true, 80, 24, CancellationToken.None);
            throw new Exception("Elevated ConPTY startup should be rejected.");
        }
        catch (NotSupportedException) { }

        if (!OperatingSystem.IsWindows())
        {
            if (backend.IsAvailable) throw new Exception("ConPTY must report unavailable off Windows.");
            return;
        }

        await using var session = await backend.StartAsync(false, 80, 24, CancellationToken.None);
        await session.ResizeAsync(100, 30, CancellationToken.None);
        var output = new MemoryStream();
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = session.Output.CopyToAsync(output, readCts.Token);
        var command = Encoding.UTF8.GetBytes("echo XAS_CONPTY_OK\r\nexit /b 7\r\n");
        await session.Input.WriteAsync(command);
        await session.Input.FlushAsync();
        var exitCode = await session.WaitForExitAsync(readCts.Token);
        await read;
        var text = Encoding.UTF8.GetString(output.ToArray());
        if (exitCode != 7) throw new Exception($"Expected exit code 7, got {exitCode}. Output: {text}");
        if (!text.Contains("XAS_CONPTY_OK", StringComparison.Ordinal))
            throw new Exception("ConPTY output did not contain the submitted command marker.");
    }
}
