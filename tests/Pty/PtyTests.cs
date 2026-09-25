using Xas.Daemon.Shell;
using System.Text;

namespace Xas.Tests;

public static class PtyTests
{
    public static async Task RunAsync()
    {
        var backend = new LinuxPtyBackend("definitely-missing-xas-pty-helper");
        if (backend.IsAvailable) throw new Exception("A missing Linux PTY helper was advertised as available.");
        try
        {
            await backend.StartAsync(true, 80, 24, CancellationToken.None);
            throw new Exception("Elevated Linux PTY was accepted without a privilege broker.");
        }
        catch (NotSupportedException) { }
        if (OperatingSystem.IsLinux())
        {
            try { await backend.StartAsync(false, 80, 24, CancellationToken.None); throw new Exception("Missing helper was accepted."); }
            catch (FileNotFoundException) { }

            var installed = new LinuxPtyBackend();
            if (installed.IsAvailable)
            {
                await using var session = await installed.StartAsync(false, 80, 24, CancellationToken.None);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await session.ResizeAsync(100, 30, timeout.Token);
                using var output = new MemoryStream();
                var read = session.Output.CopyToAsync(output, timeout.Token);
                await session.Input.WriteAsync(Encoding.UTF8.GetBytes("echo XAS_LINUX_PTY_OK\nexit\n"), timeout.Token);
                var exit = await session.WaitForExitAsync(timeout.Token);
                await read;
                if (exit != 0 || !Encoding.UTF8.GetString(output.ToArray()).Contains("XAS_LINUX_PTY_OK", StringComparison.Ordinal))
                    throw new Exception("Linux PTY shell did not return the expected output and exit code.");
            }
        }
    }
}
