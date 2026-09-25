using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Xas.Core;

/// <summary>Plain-text clipboard access through the current Linux desktop session tools.</summary>
/// <remarks>
/// Wayland uses wl-clipboard (wl-paste/wl-copy); X11 uses xclip or xsel. The process runs in
/// the daemon's user session and inherits its display environment. No shell is involved.
/// </remarks>
public sealed class LinuxTextClipboard : ITextClipboardBackend
{
    private const int MaxTextBytes = 256 * 1024;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string? _pasteProgram;
    private readonly string? _copyProgram;
    private readonly SessionKind _session;

    public LinuxTextClipboard()
    {
        if (!OperatingSystem.IsLinux()) return;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            _pasteProgram = FindOnPath("wl-paste");
            _copyProgram = FindOnPath("wl-copy");
            if (_pasteProgram is not null && _copyProgram is not null) _session = SessionKind.Wayland;
        }

        if (_session == SessionKind.None && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            var xclip = FindOnPath("xclip");
            var xsel = FindOnPath("xsel");
            if (xclip is not null) { _pasteProgram = xclip; _copyProgram = xclip; _session = SessionKind.Xclip; }
            else if (xsel is not null) { _pasteProgram = xsel; _copyProgram = xsel; _session = SessionKind.Xsel; }
        }
    }

    public bool IsAvailable => OperatingSystem.IsLinux() && _session != SessionKind.None;

    public async ValueTask<ClipboardTextSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var args = _session switch
        {
            SessionKind.Wayland => new[] { "--no-newline", "--type", "text/plain" },
            SessionKind.Xclip => new[] { "-quiet", "-selection", "clipboard", "-out", "-target", "UTF8_STRING" },
            SessionKind.Xsel => new[] { "--clipboard", "--output" },
            _ => throw new PlatformNotSupportedException()
        };
        var bytes = await RunCaptureAsync(_pasteProgram!, args, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaxTextBytes) throw new InvalidDataException("Clipboard text exceeds the 256 KiB limit.");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Clipboard content is not valid UTF-8 text.", ex); }
        return new ClipboardTextSnapshot(text, StableChangeId(bytes));
    }

    public async ValueTask SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = StrictUtf8.GetBytes(text);
        if (bytes.Length > MaxTextBytes) throw new ArgumentOutOfRangeException(nameof(text), "Clipboard text exceeds the 256 KiB limit.");
        var args = _session switch
        {
            SessionKind.Wayland => new[] { "--type", "text/plain" },
            SessionKind.Xclip => new[] { "-quiet", "-selection", "clipboard", "-in", "-target", "UTF8_STRING" },
            SessionKind.Xsel => new[] { "--clipboard", "--input" },
            _ => throw new PlatformNotSupportedException()
        };
        await RunWriteAsync(_copyProgram!, args, bytes, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureAvailable()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux clipboard access is available only on Linux.");
        if (!IsAvailable) throw new PlatformNotSupportedException("No supported clipboard tool is available for the current Linux desktop session.");
    }

    private static async Task<byte[]> RunCaptureAsync(string executable, string[] arguments, CancellationToken token)
    {
        using var process = Start(executable, arguments, redirectInput: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProcessTimeout);
        using var registration = timeout.Token.Register(Kill, process);
        using var output = new MemoryStream();
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var readTask = CopyBoundedAsync(process.StandardOutput.BaseStream, output, MaxTextBytes + 1, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), readTask, stderrTask).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            Kill(process);
            throw;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Clipboard command did not finish within five seconds.");
        }
        if (process.ExitCode != 0) throw new IOException($"Clipboard command '{Path.GetFileName(executable)}' failed with exit code {process.ExitCode}.");
        return output.ToArray();
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, int limit, CancellationToken token)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            if (total > limit) throw new InvalidDataException("Clipboard command returned more than the allowed text size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }

    private static async Task RunWriteAsync(string executable, string[] arguments, byte[] bytes, CancellationToken token)
    {
        using var process = Start(executable, arguments, redirectInput: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProcessTimeout);
        using var registration = timeout.Token.Register(Kill, process);
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stderrTask, stdoutTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Clipboard command did not finish within five seconds.");
        }
        if (process.ExitCode != 0) throw new IOException($"Clipboard command '{Path.GetFileName(executable)}' failed with exit code {process.ExitCode}.");
    }

    private static Process Start(string executable, string[] arguments, bool redirectInput)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // X11 commands do not need stdout for writes. Keeping it redirected avoids a child
        // attaching to an unrelated terminal; stdout is drained to prevent accidental blocking.
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException($"Could not start clipboard command '{Path.GetFileName(executable)}'.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static void Kill(object? state)
    {
        if (state is not Process process) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static ulong StableChangeId(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        return null;
    }

    private enum SessionKind { None, Wayland, Xclip, Xsel }
}
