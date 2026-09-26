using System.Diagnostics;
using Xas.Core;

namespace Xas.Daemon.Input;

/// <summary>
/// Input injection through a uinput virtual device, the mechanism Sunshine, Moonlight, and Parsec use on a
/// Linux host. It works on any session with no portal and therefore no consent prompt, and the pointer is
/// advertised as a direct device with an absolute axis range so the compositor maps motion to the screen
/// one-to-one instead of running it through pointer acceleration.
/// </summary>
public sealed class LinuxUinputInputBackend(Func<DisplayMetadata?>? resolveDisplay = null) :
    IAbsoluteInputInjectionBackend, IInputBatchInjectionBackend, IInputActivationBackend, IDisposable
{
    /// <summary>Environment override, mirroring the Wayland helper's, for unusual install locations.</summary>
    public const string HelperVariable = "XAS_UINPUT_HELPER";

    private readonly object _gate = new();
    private Process? _helper;
    private Stream? _input;
    private string? _lastError;
    private bool _ready;
    private bool _disposed;
    private int _width, _height;

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || !OperatingSystem.IsLinux()) return false;
                var helper = FindHelper();
                if (helper is null) return false;
                try
                {
                    using var probe = Process.Start(new ProcessStartInfo(helper, "--probe")
                    {
                        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                        CreateNoWindow = true
                    });
                    if (probe is null) return false;
                    probe.WaitForExit(1500);
                    if (!probe.HasExited) { probe.Kill(true); return false; }
                    return probe.ExitCode == 0;
                }
                catch { return false; }
            }
        }
    }

    /// <summary>
    /// Creates the devices. There is nothing to consent to, so unlike the portal this resolves immediately
    /// or not at all; the only failure mode is a helper that cannot open /dev/uinput.
    /// </summary>
    public async ValueTask ActivateAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ready && _helper is { HasExited: false }) return;
            if (_helper is not null) StopHelperCore();
        }
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var path = FindHelper() ?? throw new PlatformNotSupportedException(
            "Linux input requires xas-uinput built against linux/uinput.h and available in PATH or " +
            $"{HelperVariable}. Set a udev rule granting write access to /dev/uinput.");
        var display = resolveDisplay?.Invoke();
        var width = display?.WidthPixels is > 0 ? display.WidthPixels : 1920;
        var height = display?.HeightPixels is > 0 ? display.HeightPixels : 1080;
        var p = Process.Start(new ProcessStartInfo(path, $"{width} {height}")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        }) ?? throw new IOException("Could not start xas-uinput.");
        lock (_gate)
        {
            _helper = p; _input = p.StandardInput.BaseStream; _width = width; _height = height; _ready = false;
        }
        // Drain stderr so the helper can never block writing to a full pipe mid-stream.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await p.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                    lock (_gate) _lastError = line;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        string? reply;
        try { reply = await p.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { StopHelper(); throw; }
        if (reply != "READY")
        {
            var detail = _lastError ?? reply ?? $"helper exited with code {SafeExitCode(p)}";
            StopHelper();
            throw new IOException($"uinput device setup failed: {detail}");
        }
        lock (_gate) _ready = true;
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            Stream input;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                input = _input ?? throw new IOException("The uinput helper is not running.");
            }
            await input.WriteAsync(LinuxInputHelperWire.Encode(events), timeout.Token).ConfigureAwait(false);
            await input.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            StopHelper();
            throw new IOException("The uinput helper stopped accepting input.", ex);
        }
    }

    public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream? input;
        lock (_gate) { input = _input; }
        if (input is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await input.WriteAsync(LinuxInputHelperWire.Release, timeout.Token).ConfigureAwait(false);
            await input.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { StopHelper(); }
    }

    public void Dispose()
    {
        StopHelper();
        lock (_gate) _disposed = true;
    }

    private void StopHelper() { lock (_gate) StopHelperCore(); }

    private void StopHelperCore()
    {
        if (_helper is { } helper)
        {
            try { if (!helper.HasExited) helper.Kill(true); } catch { }
            helper.Dispose();
        }
        _helper = null; _input = null; _ready = false;
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.HasExited ? p.ExitCode : -1; } catch { return -1; }
    }

    /// <summary>Prefers a helper beside the daemon, then PATH, then the environment override.</summary>
    private static string? FindHelper()
    {
        var overridePath = Environment.GetEnvironmentVariable(HelperVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;
        var beside = Path.Combine(AppContext.BaseDirectory, "xas-uinput");
        if (File.Exists(beside)) return beside;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p, "xas-uinput")).FirstOrDefault(File.Exists);
    }
}
