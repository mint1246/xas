using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Xas.Core;

/// <summary>Plain-text clipboard access through the current Linux desktop session tools.</summary>
/// <remarks>
/// Wayland uses wl-clipboard (wl-paste/wl-copy); X11 uses xclip or xsel. The process runs in
/// the daemon's user session and inherits its display environment. No shell is involved.
/// </remarks>
public sealed class LinuxTextClipboard : ITextClipboardBackend, IClipboardChangeSource
{
    private const int MaxTextBytes = 256 * 1024;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string? _pasteProgram;
    private readonly string? _copyProgram;
    private readonly SessionKind _session;
    private readonly string? _availabilityError;

    public LinuxTextClipboard()
    {
        if (!OperatingSystem.IsLinux()) return;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            _pasteProgram = FindOnPath("wl-paste");
            _copyProgram = FindOnPath("wl-copy");
            if (_pasteProgram is not null && _copyProgram is not null) _session = SessionKind.Wayland;
            else _availabilityError = "Wayland session detected, but wl-paste/wl-copy from wl-clipboard are not both available in PATH.";
        }

        if (_session == SessionKind.None && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            var xclip = FindOnPath("xclip");
            var xsel = FindOnPath("xsel");
            if (xclip is not null) { _pasteProgram = xclip; _copyProgram = xclip; _session = SessionKind.Xclip; }
            else if (xsel is not null) { _pasteProgram = xsel; _copyProgram = xsel; _session = SessionKind.Xsel; }
            else if (_availabilityError is null) _availabilityError = "X11 session detected, but neither xclip nor xsel is available in PATH.";
        }

        if (_session == SessionKind.None && _availabilityError is null)
            _availabilityError = "No graphical Linux clipboard session was detected. WAYLAND_DISPLAY and DISPLAY are both unset.";
    }

    public bool IsAvailable => OperatingSystem.IsLinux() && _session != SessionKind.None;

    public async ValueTask<ClipboardTextSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var args = _session switch
        {
            SessionKind.Wayland => new[] { "--no-newline", "--type", "text" },
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
            SessionKind.Wayland => new[] { "--type", "text/plain;charset=utf-8" },
            SessionKind.Xclip => new[] { "-quiet", "-selection", "clipboard", "-in", "-target", "UTF8_STRING" },
            SessionKind.Xsel => new[] { "--clipboard", "--input" },
            _ => throw new PlatformNotSupportedException()
        };
        await RunWriteAsync(_copyProgram!, args, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Uses wl-paste watch notifications on supported Wayland compositors, with polling fallbacks elsewhere.</summary>
    public async IAsyncEnumerable<ClipboardTextSnapshot> WatchChangesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var previous = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        yield return previous;
        if (_session == SessionKind.Wayland)
        {
            var changes = Channel.CreateBounded<ClipboardTextSnapshot>(new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            var producer = ProduceWaylandChangesAsync(previous, changes.Writer, cancellationToken);
            await foreach (var current in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                previous = current;
                yield return current;
            }
            await producer.ConfigureAwait(false);
            yield break;
        }

        await foreach (var current in PollChangesAsync(previous, cancellationToken).ConfigureAwait(false))
            yield return current;
    }

    private void EnsureAvailable()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux clipboard access is available only on Linux.");
        if (!IsAvailable) throw new PlatformNotSupportedException(_availabilityError ?? "No supported clipboard tool is available for the current Linux desktop session.");
    }

    private async Task ProduceWaylandChangesAsync(ClipboardTextSnapshot previous,
        ChannelWriter<ClipboardTextSnapshot> writer, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            previous = await WatchWaylandAsync(previous, writer, token).ConfigureAwait(false);
            if (!token.IsCancellationRequested)
                await PollChangesToWriterAsync(previous, writer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // wl-paste --watch requires the compositor's data-control protocol. GNOME/KDE setups
            // that do not expose it still get functional clipboard sync through the local poller.
            try { await PollChangesToWriterAsync(previous, writer, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception fallback) { failure = new IOException($"Wayland clipboard watch failed ({ex.Message}); polling fallback also failed ({fallback.Message}).", fallback); }
        }
        finally { writer.TryComplete(failure); }
    }

    private async Task<ClipboardTextSnapshot> WatchWaylandAsync(ClipboardTextSnapshot previous,
        ChannelWriter<ClipboardTextSnapshot> writer, CancellationToken token)
    {
        var shell = FindOnPath("sh") ?? (File.Exists("/bin/sh") ? "/bin/sh" : null)
            ?? throw new PlatformNotSupportedException("Wayland clipboard watch needs a POSIX sh executable.");
        using var process = Start(_pasteProgram!, ["--type", "text", "--watch", shell, "-c", "cat >/dev/null; printf x"], redirectInput: false);
        using var registration = token.Register(Kill, process);
        using var stderr = new MemoryStream();
        var stderrTask = CopyBoundedAsync(process.StandardError.BaseStream, stderr, 16 * 1024, token);
        var signal = new byte[64];
        while (true)
        {
            var count = await process.StandardOutput.BaseStream.ReadAsync(signal, token).ConfigureAwait(false);
            if (count == 0) break;
            ClipboardTextSnapshot current;
            try { current = await GetSnapshotAsync(token).ConfigureAwait(false); }
            catch (IOException) { continue; } // Current selection may not contain text.
            if (current.ChangeId == previous.ChangeId && current.Text == previous.Text) continue;
            previous = current;
            await writer.WriteAsync(current, token).ConfigureAwait(false);
        }
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);
        if (!token.IsCancellationRequested)
            throw new IOException(FormatFailure("wl-paste --watch", process.ExitCode, stderr.ToArray()));
        return previous;
    }

    private async IAsyncEnumerable<ClipboardTextSnapshot> PollChangesAsync(ClipboardTextSnapshot previous,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            ClipboardTextSnapshot current;
            try { current = await GetSnapshotAsync(token).ConfigureAwait(false); }
            catch (IOException) { continue; }
            if (current.ChangeId == previous.ChangeId && current.Text == previous.Text) continue;
            previous = current;
            yield return current;
        }
    }

    private async Task PollChangesToWriterAsync(ClipboardTextSnapshot previous,
        ChannelWriter<ClipboardTextSnapshot> writer, CancellationToken token)
    {
        await foreach (var current in PollChangesAsync(previous, token).ConfigureAwait(false))
        {
            previous = current;
            await writer.WriteAsync(current, token).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> RunCaptureAsync(string executable, string[] arguments, CancellationToken token)
    {
        using var process = Start(executable, arguments, redirectInput: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProcessTimeout);
        using var registration = timeout.Token.Register(Kill, process);
        using var output = new MemoryStream();
        using var stderr = new MemoryStream();
        var stderrTask = CopyBoundedAsync(process.StandardError.BaseStream, stderr, 16 * 1024, timeout.Token);
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
        if (process.ExitCode != 0) throw new IOException(FormatFailure(Path.GetFileName(executable), process.ExitCode, stderr.ToArray()));
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
        using var stderr = new MemoryStream();
        var stderrTask = CopyBoundedAsync(process.StandardError.BaseStream, stderr, 16 * 1024, timeout.Token);
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
        if (process.ExitCode != 0) throw new IOException(FormatFailure(Path.GetFileName(executable), process.ExitCode, stderr.ToArray()));
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

    private static string FormatFailure(string command, int exitCode, byte[] stderr)
    {
        var detail = Encoding.UTF8.GetString(stderr).Trim();
        return detail.Length == 0
            ? $"Clipboard command '{command}' failed with exit code {exitCode}. Check WAYLAND_DISPLAY/DISPLAY, XDG_RUNTIME_DIR, and clipboard tool availability."
            : $"Clipboard command '{command}' failed with exit code {exitCode}: {detail}";
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
