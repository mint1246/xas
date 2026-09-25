using Xas.Cli.Terminal;

namespace Xas.Tests;

public static class TerminalTests
{
    public static Task RunAsync()
    {
        if (OperatingSystem.IsLinux())
        {
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
            }
            return Task.CompletedTask;
        }
        if (!OperatingSystem.IsWindows())
        {
            try { TerminalMode.Enter(); throw new Exception("Expected unsupported platform failure."); }
            catch (PlatformNotSupportedException) { }
            return Task.CompletedTask;
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
        return Task.CompletedTask;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(uint nStdHandle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
}
