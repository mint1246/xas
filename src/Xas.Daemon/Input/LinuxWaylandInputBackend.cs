using System.Diagnostics;
using Xas.Core;

namespace Xas.Daemon.Input;

/// <summary>Wayland input injection through an XDG RemoteDesktop portal EIS session.</summary>
public sealed class LinuxWaylandInputBackend : IAbsoluteInputInjectionBackend, IInputBatchInjectionBackend, IInputActivationBackend, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private Process? _helper;
    private Stream? _input;
    private StreamReader? _output;
    private bool _ready;
    private bool _disposed;
    private string? _lastHelperError;
    private string? _reportedHelperError;

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || !OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return false;
                var helper = FindHelper();
                if (helper is null) return false;
                try
                {
                    using var probe = Process.Start(new ProcessStartInfo(helper, "--probe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
                    if (probe is null) return false;
                    probe.WaitForExit(1500);
                    if (!probe.HasExited) { probe.Kill(true); return false; }
                    return probe.ExitCode == 0;
                }
                catch { return false; }
            }
        }
    }

    public async ValueTask ActivateAsync(CancellationToken cancellationToken)
    {
        await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process process;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_ready && _helper is { HasExited: false }) return;
                StartHelper();
                process = _helper!;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            string? reply;
            try { reply = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                lock (_gate) StopHelper();
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("Timed out waiting for RemoteDesktop portal consent and EIS device setup.");
            }
            lock (_gate)
            {
                if (reply != "READY")
                {
                    // The helper reports why on stderr and then exits, so drain it before stopping the
                    // process. Checking HasExited first races the exit and loses the reason.
                    var detail = reply;
                    try
                    {
                        process.WaitForExit(2000);
                        var stderr = process.StandardError.ReadToEnd().Trim();
                        if (stderr.Length > 0) detail = stderr;
                        else if (detail is null or "") detail = $"helper exited with code {process.ExitCode}";
                    }
                    catch (InvalidOperationException) { /* The process is still running; the reply stands. */ }
                    StopHelper();
                    throw new IOException($"Wayland RemoteDesktop portal/EIS setup failed: {detail}");
                }
                _ready = true;
            }
        }
        finally { _activationGate.Release(); }
    }

    public async ValueTask InjectAsync(InputEvent inputEvent, CancellationToken cancellationToken)
    {
        await InjectBatchAsync([inputEvent], cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask InjectBatchAsync(IReadOnlyList<InputEvent> events, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (events.Count == 0) return;
        await ActivateAsync(cancellationToken).ConfigureAwait(false);
        await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                Stream input;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    input = _input ?? throw new IOException("Wayland input helper is not running.");
                }
                // Fire and forget. Waiting for a per-event acknowledgement put a full round trip in the
                // input path, and the resulting timeouts tore the session down and re-prompted for consent.
                // The helper applies each event as it reads it and reports trouble on stderr instead.
                await input.WriteAsync(LinuxInputHelperWire.Encode(events), timeout.Token).ConfigureAwait(false);
                await input.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A failed write means the helper is gone, which is the one case that ends the session.
                lock (_gate) StopHelper();
                throw new IOException("Wayland input helper is not running.", ex);
            }
        }
        finally { _activationGate.Release(); }
    }

    public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Stream? input;
            StreamReader? output;
            lock (_gate) { input = _input; output = _output; }
            if (input is null || output is null) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                await input.WriteAsync(LinuxInputHelperWire.Release, timeout.Token).ConfigureAwait(false);
                await input.FlushAsync(timeout.Token).ConfigureAwait(false);
                if (await output.ReadLineAsync(timeout.Token).ConfigureAwait(false) == "OK") return;
                // Keep the consented portal session for the next monitor crossing; a failed release is
                // reported but does not justify discarding the user's consent.
                throw new IOException("Wayland input helper did not release held input.");
            }
            catch (Exception ex) when (ex is not IOException) { lock (_gate) StopHelper(); throw; }
        }
        finally { _activationGate.Release(); }
    }

    private void StartHelper()
    {
        if (_helper is { HasExited: false }) { _ready = false; throw new InvalidOperationException("Wayland EIS helper is running without a completed activation."); }
        if (_helper is not null) StopHelper();
        var path = FindHelper() ?? throw new PlatformNotSupportedException("Wayland input requires xas-wayland-eis built with libei and liboeffis and available in PATH or XAS_WAYLAND_EIS_HELPER.");
        var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })
            ?? throw new IOException("Could not start xas-wayland-eis.");
        _helper = p; _input = p.StandardInput.BaseStream; _output = p.StandardOutput;
        _ready = false;
        // Drain stderr continuously. The helper reports per-event problems there, and an unread pipe would
        // fill and block the helper mid-stream, which shows up as the cursor stalling.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await p.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (_gate)
                    {
                        _lastHelperError = line;
                        if (line.Length > 0) _reportedHelperError = line;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
    }

    private static string? FindHelper()
    {
        var configured = Environment.GetEnvironmentVariable("XAS_WAYLAND_EIS_HELPER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var name = OperatingSystem.IsWindows() ? "xas-wayland-eis.exe" : "xas-wayland-eis";
        var adjacent = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(adjacent)) return adjacent;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
    }

    private void StopHelper()
    {
        try { if (_helper is { HasExited: false }) { _helper.Kill(true); _helper.WaitForExit(1000); } } catch { }
        _input?.Dispose(); _output?.Dispose(); _helper?.Dispose(); _input = null; _output = null; _helper = null; _ready = false;
    }

    public void Dispose() { lock (_gate) { if (_disposed) return; StopHelper(); _disposed = true; } }
}
