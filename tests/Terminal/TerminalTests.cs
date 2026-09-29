using Xas.Cli.Terminal;

namespace Xas.Tests;

public static class TerminalTests
{
    public static async Task RunAsync()
    {
        if (OperatingSystem.IsLinux())
        {
            using (var input = TerminalMode.OpenInputStream())
            {
                if (input is not FileStream)
                    throw new Exception("Linux interactive input must bypass Console's line-oriented stream.");
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
                catch (InvalidOperationException) { }
            }
            else
            {
                using var mode = TerminalMode.Enter();
                if (mode.Columns < 1 || mode.Rows < 1) throw new Exception("Invalid terminal dimensions.");
                var before = ReadLinuxLocalFlags();
                if ((before & (0x8u | 0x2u)) != 0)
                    throw new Exception("Linux terminal did not enter raw no-echo mode.");
                for (var i = 0; i < 8; i++)
                {
                    _ = TerminalMode.CurrentSize();
                    await Task.Delay(30).ConfigureAwait(false);
                }
                var after = ReadLinuxLocalFlags();
                if ((after & (0x8u | 0x2u)) != 0)
                    throw new Exception("Terminal size polling changed raw no-echo mode.");
            }
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            try { TerminalMode.Enter(); throw new Exception("Expected unsupported platform failure."); }
            catch (PlatformNotSupportedException) { }
            return;
        }

        // The test runner is often redirected, so mode round-trip can only be exercised
        // when launched from an attached console.
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            uint inputBefore = 0, outputBefore = 0;
            var input = GetStdHandle(unchecked((uint)-10));
            var output = GetStdHandle(unchecked((uint)-11));
            if (GetConsoleMode(input, out inputBefore) && GetConsoleMode(output, out outputBefore))
            {
                var mode = TerminalMode.Enter();
                if (mode.Columns < 1 || mode.Rows < 1) throw new Exception("Invalid terminal dimensions.");
                mode.Dispose();
                if (!GetConsoleMode(input, out var inputAfter) || inputAfter != inputBefore ||
                    !GetConsoleMode(output, out var outputAfter) || outputAfter != outputBefore)
                    throw new Exception("Terminal modes were not restored exactly.");
            }
        }
    }

    private static uint ReadLinuxLocalFlags()
    {
        var termios = System.Runtime.InteropServices.Marshal.AllocHGlobal(64);
        try
        {
            if (GetLinuxTermios(0, termios) != 0)
                throw new Exception("Could not inspect Linux terminal settings.");
            return unchecked((uint)System.Runtime.InteropServices.Marshal.ReadInt32(termios, 12));
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(termios); }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    private static extern int GetLinuxTermios(int fd, IntPtr termios);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(uint nStdHandle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
}
