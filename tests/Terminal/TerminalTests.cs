using Xas.Cli.Terminal;
using System.Runtime.InteropServices;

namespace Xas.Tests;

public static class TerminalTests
{
    public static async Task RunAsync()
    {
        if (OperatingSystem.IsLinux())
        {
            await CheckLinuxTerminalAsync();
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            CheckUnsupportedPlatform();
            return;
        }

        CheckWindowsTerminalMode();
    }

    private static async Task CheckLinuxTerminalAsync()
    {
        using (var input = TerminalMode.OpenInputStream())
        {
            if (input is not FileStream)
            {
                throw new Exception(
                    "Linux interactive input must bypass Console's line-oriented stream.");
            }
        }

        // CI usually has pipes rather than a controlling terminal. The attached-console
        // path is exercised when the harness is run from an interactive shell.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            try
            {
                TerminalMode.Enter();
                throw new Exception("Expected an attached-terminal failure.");
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        using var mode = TerminalMode.Enter();
        if (mode.Columns < 1 || mode.Rows < 1)
        {
            throw new Exception("Invalid terminal dimensions.");
        }

        var before = ReadLinuxLocalFlags();
        if ((before & (0x8u | 0x2u)) != 0)
        {
            throw new Exception("Linux terminal did not enter raw no-echo mode.");
        }

        for (var poll = 0; poll < 8; poll++)
        {
            _ = TerminalMode.CurrentSize();
            await Task.Delay(30).ConfigureAwait(false);
        }

        var after = ReadLinuxLocalFlags();
        if ((after & (0x8u | 0x2u)) != 0)
        {
            throw new Exception("Terminal size polling changed raw no-echo mode.");
        }
    }

    private static void CheckUnsupportedPlatform()
    {
        try
        {
            TerminalMode.Enter();
            throw new Exception("Expected unsupported platform failure.");
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void CheckWindowsTerminalMode()
    {
        // The test runner is often redirected, so mode round-trip can only be exercised
        // when launched from an attached console.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return;
        }

        uint inputBefore = 0, outputBefore = 0;
        var input = GetStdHandle(unchecked((uint)-10));
        var output = GetStdHandle(unchecked((uint)-11));
        if (!GetConsoleMode(input, out inputBefore) ||
            !GetConsoleMode(output, out outputBefore))
        {
            return;
        }

        var mode = TerminalMode.Enter();
        if (mode.Columns < 1 || mode.Rows < 1)
        {
            throw new Exception("Invalid terminal dimensions.");
        }

        mode.Dispose();
        if (!GetConsoleMode(input, out var inputAfter) || inputAfter != inputBefore ||
            !GetConsoleMode(output, out var outputAfter) || outputAfter != outputBefore)
        {
            throw new Exception("Terminal modes were not restored exactly.");
        }
    }

    private static uint ReadLinuxLocalFlags()
    {
        var termios = Marshal.AllocHGlobal(64);
        try
        {
            if (GetLinuxTermios(0, termios) != 0)
                throw new Exception("Could not inspect Linux terminal settings.");
            return unchecked((uint)Marshal.ReadInt32(termios, 12));
        }
        finally
        {
            Marshal.FreeHGlobal(termios);
        }
    }

    [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    private static extern int GetLinuxTermios(int fd, IntPtr termios);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(uint nStdHandle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);
}
