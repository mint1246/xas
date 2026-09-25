using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Xas.Cli.Terminal;

/// <summary>Temporarily puts the current Windows or Linux terminal into character-at-a-time mode.</summary>
public sealed class TerminalMode : IDisposable
{
    private const uint StdInputHandle = unchecked((uint)-10);
    private const uint StdOutputHandle = unchecked((uint)-11);
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint Utf8CodePage = 65001;

    private readonly IntPtr _inputHandle;
    private readonly IntPtr _outputHandle;
    private readonly uint _oldInputMode;
    private readonly uint _oldOutputMode;
    private readonly uint _oldInputCodePage;
    private readonly uint _oldOutputCodePage;
    private readonly string? _unixSavedMode;
    private bool _disposed;

    public int Columns { get; }
    public int Rows { get; }

    private TerminalMode(IntPtr inputHandle, IntPtr outputHandle, uint oldInputMode, uint oldOutputMode,
        uint oldInputCodePage, uint oldOutputCodePage)
    {
        _inputHandle = inputHandle;
        _outputHandle = outputHandle;
        _oldInputMode = oldInputMode;
        _oldOutputMode = oldOutputMode;
        _oldInputCodePage = oldInputCodePage;
        _oldOutputCodePage = oldOutputCodePage;
        Columns = Console.WindowWidth;
        Rows = Console.WindowHeight;
    }

    private TerminalMode(string savedMode, int columns, int rows)
    {
        _unixSavedMode = savedMode;
        Columns = columns;
        Rows = rows;
    }

    /// <summary>Enables raw character input and VT sequences on the current terminal.</summary>
    public static TerminalMode Enter()
    {
        if (OperatingSystem.IsLinux()) return EnterLinux();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Raw terminal mode is supported on Windows and Linux.");

        var input = GetStdHandle(StdInputHandle);
        var output = GetStdHandle(StdOutputHandle);
        if (input == IntPtr.Zero || input == new IntPtr(-1) || output == IntPtr.Zero || output == new IntPtr(-1))
            throw new InvalidOperationException("The process does not have usable console handles.");
        if (!GetConsoleMode(input, out var inputMode) || !GetConsoleMode(output, out var outputMode))
            throw new InvalidOperationException("Raw terminal mode requires an attached Windows console.");
        var inputCodePage = GetConsoleCP();
        var outputCodePage = GetConsoleOutputCP();
        if (inputCodePage == 0 || outputCodePage == 0)
            throw new InvalidOperationException("Could not read the Windows console code pages.");

        var rawInput = (inputMode & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput)) | EnableVirtualTerminalInput;
        var vtOutput = outputMode | EnableVirtualTerminalProcessing;
        if (!SetConsoleMode(input, rawInput))
            throw new InvalidOperationException("Could not enable raw input mode.");
        if (!SetConsoleMode(output, vtOutput))
        {
            SetConsoleMode(input, inputMode);
            throw new InvalidOperationException("Could not enable VT output mode.");
        }
        // The shell stream contains UTF-8 bytes. WriteFile/ReadFile on a Windows
        // console interpret those bytes using its active code pages.
        if (!SetConsoleCP(Utf8CodePage) || !SetConsoleOutputCP(Utf8CodePage))
        {
            SetConsoleOutputCP(outputCodePage);
            SetConsoleCP(inputCodePage);
            SetConsoleMode(output, outputMode);
            SetConsoleMode(input, inputMode);
            throw new InvalidOperationException("Could not enable UTF-8 console input and output.");
        }

        try
        {
            return new TerminalMode(input, output, inputMode, outputMode, inputCodePage, outputCodePage);
        }
        catch
        {
            SetConsoleOutputCP(outputCodePage);
            SetConsoleCP(inputCodePage);
            SetConsoleMode(output, outputMode);
            SetConsoleMode(input, inputMode);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_unixSavedMode is not null)
        {
            RunStty(_unixSavedMode);
            return;
        }
        var outputCodePageRestored = SetConsoleOutputCP(_oldOutputCodePage);
        var inputCodePageRestored = SetConsoleCP(_oldInputCodePage);
        var outputRestored = SetConsoleMode(_outputHandle, _oldOutputMode);
        var inputRestored = SetConsoleMode(_inputHandle, _oldInputMode);
        if (!outputCodePageRestored || !inputCodePageRestored || !outputRestored || !inputRestored)
            throw new InvalidOperationException("Could not restore the original console settings.");
    }

    public static (ushort Columns, ushort Rows) CurrentSize()
    {
        if (OperatingSystem.IsWindows())
            return (checked((ushort)Console.WindowWidth), checked((ushort)Console.WindowHeight));
        if (OperatingSystem.IsLinux())
        {
            var (columns, rows) = ReadLinuxSize();
            return (checked((ushort)columns), checked((ushort)rows));
        }
        throw new PlatformNotSupportedException("Terminal size is supported on Windows and Linux.");
    }

    /// <summary>Use UTF-8 for raw command output written to an attached Windows console.</summary>
    public static IDisposable EnterUtf8Output()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected) return NoopScope.Instance;
        var output = GetStdHandle(StdOutputHandle);
        if (output == IntPtr.Zero || output == new IntPtr(-1) || !GetConsoleMode(output, out _))
            return NoopScope.Instance;
        var oldCodePage = GetConsoleOutputCP();
        if (oldCodePage == 0 || !SetConsoleOutputCP(Utf8CodePage))
            throw new InvalidOperationException("Could not enable UTF-8 console output.");
        return new OutputCodePageScope(oldCodePage);
    }

    private sealed class OutputCodePageScope(uint oldCodePage) : IDisposable
    {
        public void Dispose()
        {
            if (!SetConsoleOutputCP(oldCodePage))
                throw new InvalidOperationException("Could not restore the original console output code page.");
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    private static TerminalMode EnterLinux()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new InvalidOperationException("Interactive mode requires an attached terminal on stdin and stdout.");

        var saved = RunStty("-g").Trim();
        if (saved.Length == 0 || saved.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ':' or '-')))
            throw new InvalidOperationException("stty returned an invalid terminal mode.");
        var (columns, rows) = ReadLinuxSize();

        try
        {
            RunStty("raw", "-echo");
            return new TerminalMode(saved, columns, rows);
        }
        catch
        {
            try { RunStty(saved); } catch { }
            throw;
        }
    }

    private static (int Columns, int Rows) ReadLinuxSize()
    {
        var dimensions = RunStty("size").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out var rows) ||
            !int.TryParse(dimensions[1], out var columns) || rows < 1 || columns < 1)
            throw new InvalidOperationException("Could not read terminal dimensions from stty.");
        return (columns, rows);
    }

    // stdin stays inherited so stty addresses the caller's controlling terminal. Arguments
    // are passed directly to the process, never through a shell.
    private static string RunStty(params string[] arguments)
    {
        var start = new ProcessStartInfo("stty")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start stty.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"stty failed: {error.Trim()}");
        return output;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(uint nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCP(uint wCodePageID);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);
}
