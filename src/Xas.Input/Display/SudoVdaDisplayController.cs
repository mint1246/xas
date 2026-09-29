using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Xas.Core;
using Xas.Core.Configuration;

namespace Xas.Input.Display;

/// <summary>Display adapters that present software monitors, identified by their PnP hardware ID.</summary>
public static class VirtualDisplayDrivers
{
    private static readonly string[] KnownAdapterHardwareIds = [SudoVdaDriver.AdapterHardwareId];

    /// <summary>True when the adapter hardware ID belongs to a virtual display driver.</summary>
    public static bool IsVirtualAdapter(string? hardwareId) => hardwareId is not null &&
        KnownAdapterHardwareIds.Any(id => hardwareId.StartsWith(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the monitor was published by a virtual display driver rather than real hardware.</summary>
    public static bool IsVirtualMonitor(WindowsMonitor monitor) => IsVirtualAdapter(monitor.AdapterHardwareId);
}

/// <summary>
/// Drives the Windows monitor that input handoff is bound to, using SudoVDA, the indirect display driver
/// Apollo uses. The monitor is created at the paired Linux display's own mode, kept alive by pinging the
/// driver watchdog, and reattached automatically if Windows or the driver drops it.
/// </summary>
public sealed class SudoVdaDisplayController(Action<string>? log = null) : IVirtualDisplayController, IAsyncDisposable
{
    /// <summary>Set when the driver is present, so a missing driver is a clear message rather than a crash.</summary>
    private static readonly Lazy<bool> DriverPresent = new(static () =>
    {
        using var handle = SudoVdaDriver.Open();
        return handle is not null;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Driver fields are 13 characters plus a terminator; the prefix survives truncation intact.</summary>
    private const string MonitorDeviceName = "XAS Linux";

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SafeFileHandle? _device;
    private Guid? _ownedMonitorId;
    private string? _ownedMonitorDeviceName;
    private string? _ownedMonitorHardwareId;
    private CancellationTokenSource? _keepAlive;
    private Task? _keepAliveLoop;
    private (int Left, int Top)? _preferredPosition;

    /// <summary>
    /// Monitors this daemon created, persisted so a later run can reclaim them. SudoVDA has no request that
    /// lists monitors, so without a record a monitor left behind by a killed process is unrecoverable and the
    /// desktop accumulates one per crash. Only identities this daemon issued are ever removed.
    /// </summary>
    private static string StatePath => Path.Combine(AppPaths.Root, "virtual-display.json");

    private sealed record AttachmentRecord(Guid MonitorId, string HardwareId, string DeviceName,
        int? Left = null, int? Top = null);

    public bool IsAvailable => OperatingSystem.IsWindows() && DriverPresent.Value;

    public async ValueTask<VirtualDisplayAttachment> AttachAsync(DisplayMetadata display,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (!IsAvailable)
            throw new PlatformNotSupportedException(
                "No virtual display driver is installed. Install SudoVDA, then restart the daemon.");
        var modeWidth = ResolveVirtualWidth(display);
        var modeHeight = ResolveVirtualHeight(display);
        if (modeWidth is < 1 or > SudoVdaDriver.MaxWidthPixels ||
            modeHeight is < 1 or > SudoVdaDriver.MaxHeightPixels)
            throw new ArgumentOutOfRangeException(nameof(display),
                "The remote display mode is outside the range SudoVDA can present.");

        var refreshHertz = ResolveRefreshHertz(display.RefreshMilliHertz);
        var monitorId = MonitorIdentity.For(display.Id, modeWidth, modeHeight, refreshHertz);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // SudoVDA has no in-place reconfigure, so a mode change is a remove followed by an add. The
            // driver tears a monitor down asynchronously, so remember which panels we are about to drop and
            // wait for those specific ones to leave the desktop before asking for another. A monitor owned
            // by another application is left alone rather than waited on.
            var departing = _ownedMonitorDeviceName is { } ownedDeviceName
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    MonitorKey(ownedDeviceName, _ownedMonitorHardwareId)
                }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await DetachCoreAsync().ConfigureAwait(false);
            await WaitForRemovalAsync(departing, cancellationToken).ConfigureAwait(false);
            var device = _device ??= SudoVdaDriver.Open() ?? throw new PlatformNotSupportedException(
                "The SudoVDA control device disappeared; restart the Windows session.");
            ResolveAdapterDeviceName(cancellationToken);
            await ReclaimAbandonedAsync(device, cancellationToken).ConfigureAwait(false);

            // Each monitor on the adapter gets its own GDI device name, so the new one has to be identified by
            // what appeared rather than by the adapter name. Without this, a re-attach onto an adapter that
            // still holds another monitor would wait forever for a name that never matches.
            //
            // The deterministic identity is preferred so Windows restores the previous arrangement, but the
            // driver caches a monitor's mode against its GUID and will not republish a GUID whose monitor it
            // has already torn down, which happens on every daemon restart at an unchanged mode. Retrying
            // under a fresh identity is what makes a restart recover instead of failing forever.
            var created = await CreateAsync(device, display, refreshHertz, monitorId, cancellationToken)
                .ConfigureAwait(false)
                ?? await CreateAsync(device, display, refreshHertz, Guid.NewGuid(), cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new TimeoutException(
                    $"Windows did not publish a {modeWidth}x{modeHeight} virtual monitor.");
            monitorId = created.MonitorId;
            _ownedMonitorDeviceName = created.DeviceName;
            _ownedMonitorHardwareId = created.MonitorHardwareId;
            if (_preferredPosition is { } position)
            {
                if (WindowsMonitorTopology.TryMove(created.DeviceName, position.Left, position.Top))
                {
                    log?.Invoke($"Restored virtual display position to ({position.Left},{position.Top}).");
                    await WaitForPositionAsync(created.DeviceName, position.Left, position.Top, cancellationToken)
                        .ConfigureAwait(false);
                }
                else log?.Invoke($"Could not restore virtual display position to ({position.Left},{position.Top}).");
            }
            Remember(created);
            StartKeepAlive();
            log?.Invoke($"Virtual display {created.DeviceName} active at " +
                $"{modeWidth}x{modeHeight}@{refreshHertz}.");
            return created with { RefreshHertz = refreshHertz };
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes monitors a previous run of this daemon created but never got to detach.</summary>
    private async Task ReclaimAbandonedAsync(SafeFileHandle device, CancellationToken cancellationToken)
    {
        var records = ReadRecords();
        if (records.Count == 0) return;
        var kept = new List<AttachmentRecord>();
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var current = VirtualMonitors().FirstOrDefault(m =>
                WindowsMonitorTopology.MatchesIdentity(m, record.DeviceName, record.HardwareId));
            if (current is not null) _preferredPosition = (current.Region.Left, current.Region.Top);
            else if (record.Left is not null && record.Top is not null)
                _preferredPosition = (record.Left.Value, record.Top.Value);
            if (current is null) continue; // Already gone; drop the record after preserving its last saved position.
            try
            {
                SudoVdaDriver.Remove(device, record.MonitorId);
                removed.Add(MonitorKey(record.DeviceName, record.HardwareId));
                log?.Invoke($"Removed a virtual display abandoned by an earlier run ({record.DeviceName}).");
            }
            catch (Win32Exception)
            {
                // Keep the record so a later run can try again rather than leaking the monitor for good.
                kept.Add(record);
            }
        }
        try
        {
            await WaitForRemovalAsync(removed, cancellationToken).ConfigureAwait(false);
            WriteRecords(kept);
        }
        catch
        {
            // Keep identities in durable state until Windows confirms the corresponding monitors are gone.
            WriteRecords([.. kept, .. records.Where(r => removed.Contains(MonitorKey(r.DeviceName, r.HardwareId)))]);
            throw;
        }
    }

    private static List<AttachmentRecord> ReadRecords()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<List<AttachmentRecord>>(File.ReadAllBytes(StatePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private static void WriteRecords(List<AttachmentRecord> records)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var temp = StatePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(records));
            File.Move(temp, StatePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the record only costs the ability to reclaim a monitor after a hard kill.
        }
    }

    private void Remember(VirtualDisplayAttachment attachment)
    {
        var monitor = VirtualMonitors().FirstOrDefault(m =>
            WindowsMonitorTopology.MatchesIdentity(m, attachment.DeviceName, attachment.MonitorHardwareId));
        if (monitor is not null) _preferredPosition = (monitor.Region.Left, monitor.Region.Top);
        WriteRecords([new(attachment.MonitorId, attachment.MonitorHardwareId, attachment.DeviceName,
            _preferredPosition?.Left, _preferredPosition?.Top)]);
    }

    private static void Forget(Guid monitorId) =>
        WriteRecords([.. ReadRecords().Where(record => record.MonitorId != monitorId)]);

    /// <summary>Adds a monitor under one identity, returning null if Windows never publishes it.</summary>
    private async Task<VirtualDisplayAttachment?> CreateAsync(SafeFileHandle device, DisplayMetadata display,
        int refreshHertz, Guid monitorId, CancellationToken cancellationToken)
    {
        var before = VirtualMonitorKeys();
        // Only claim identities after Add succeeds. A failed Add can mean another application owns a
        // monitor on this adapter; adopting it would make DetachAsync remove someone else's display.
            SudoVdaDriver.Add(device, monitorId, ResolveVirtualWidth(display), ResolveVirtualHeight(display),
                refreshHertz, MonitorDeviceName);
        _ownedMonitorId = monitorId;
        var published = await TryWaitForMonitorAsync(before, display, cancellationToken).ConfigureAwait(false);
        if (published is not null)
            return new(published.DeviceName, published.HardwareId, monitorId,
                ResolveVirtualWidth(display), ResolveVirtualHeight(display), refreshHertz);
        // Leave nothing half-created behind before the next attempt uses a different identity.
        await DetachCoreAsync().ConfigureAwait(false);
        return null;
    }

    public ValueTask DetachAsync(CancellationToken cancellationToken) =>
        new(DetachGuardedAsync(cancellationToken));

    public async ValueTask DisposeAsync()
    {
        await DetachGuardedAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task DetachGuardedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await DetachCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task DetachCoreAsync()
    {
        if (_keepAlive is { } keepAlive) keepAlive.Cancel();
        if (_keepAliveLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _keepAliveLoop = null;
        _keepAlive?.Dispose();
        _keepAlive = null;
        if (_device is { } device && _ownedMonitorId is { } monitorId)
        {
            try
            {
                SudoVdaDriver.Remove(device, monitorId);
                if (_ownedMonitorDeviceName is { } deviceName)
                {
                    var departing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        MonitorKey(deviceName, _ownedMonitorHardwareId)
                    };
                    await WaitForRemovalAsync(departing, CancellationToken.None).ConfigureAwait(false);
                }
                Forget(monitorId);
            }
            catch (Win32Exception ex) { log?.Invoke($"Virtual display removal failed: {ex.Message}"); }
        }
        _ownedMonitorId = null;
        _ownedMonitorDeviceName = null;
        _ownedMonitorHardwareId = null;
    }

    private void StartKeepAlive()
    {
        var device = _device!;
        var source = _keepAlive = new CancellationTokenSource();
        _keepAliveLoop = Task.Run(async () =>
        {
            // The driver's watchdog defaults to three seconds and reaps monitors that stop being pinged.
            while (!source.IsCancellationRequested)
            {
                try { await Task.Delay(KeepAliveInterval, source.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (!SudoVdaDriver.Ping(device))
                    log?.Invoke("SudoVDA keepalive ping failed; the driver may drop the virtual display.");
                RememberCurrentPosition();
            }
        });
    }

    private void RememberCurrentPosition()
    {
        if (_ownedMonitorId is not { } monitorId || _ownedMonitorDeviceName is not { } deviceName) return;
        var hardwareId = _ownedMonitorHardwareId ?? string.Empty;
        var monitor = VirtualMonitors().FirstOrDefault(m =>
            WindowsMonitorTopology.MatchesIdentity(m, deviceName, hardwareId));
        if (monitor is null) return;
        var position = (monitor.Region.Left, monitor.Region.Top);
        var hardwareChanged = !string.IsNullOrWhiteSpace(monitor.HardwareId) &&
            !monitor.HardwareId.Equals(hardwareId, StringComparison.OrdinalIgnoreCase);
        if (!hardwareChanged && _preferredPosition == position) return;
        if (hardwareChanged) _ownedMonitorHardwareId = monitor.HardwareId;
        _preferredPosition = position;
        WriteRecords([new(monitorId, _ownedMonitorHardwareId ?? string.Empty, monitor.DeviceName,
            position.Left, position.Top)]);
        if (hardwareChanged) log?.Invoke($"Virtual display hardware ID became available ({monitor.HardwareId}).");
        log?.Invoke($"Remembered virtual display position ({position.Left},{position.Top}).");
    }

    private static async Task WaitForPositionAsync(string deviceName, int left, int top,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var monitor = WindowsMonitorTopology.Enumerate().FirstOrDefault(m =>
                m.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            if (monitor is not null && monitor.Region.Left == left && monitor.Region.Top == top) return;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for Windows to publish a monitor at the requested mode on the virtual adapter.</summary>
    private async Task<WindowsMonitor?> TryWaitForMonitorAsync(HashSet<string> before, DisplayMetadata display,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + AttachTimeout;
        var delay = TimeSpan.FromMilliseconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindNewMonitor(before, display) is { } monitor) return monitor;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 320));
        }
        return null;
    }

    /// <summary>
    /// Waits for a previously attached monitor to leave the desktop. The driver reports a removal before
    /// Windows has finished dropping the target, and adding a monitor during that window silently fails.
    /// </summary>
    private static async Task WaitForRemovalAsync(HashSet<string> departing, CancellationToken cancellationToken)
    {
        if (departing.Count == 0) return;
        var deadline = DateTime.UtcNow + AttachTimeout;
        while (true)
        {
            HashSet<string>? present = null;
            try
            {
                present = [.. WindowsMonitorTopology.Enumerate()
                    .Where(VirtualDisplayDrivers.IsVirtualMonitor).Select(MonitorKey)];
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // A failed enumeration is not proof that the driver finished removing a monitor. Keep
                // waiting so a replacement cannot race Windows' asynchronous topology update.
            }
            if (present is not null && !departing.Overlaps(present)) return;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The owned virtual display did not leave the desktop after removal.");
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Finds a monitor on the virtual adapter that was not present before the request.</summary>
    private static WindowsMonitor? FindNewMonitor(HashSet<string> before, DisplayMetadata display) =>
        VirtualMonitors().FirstOrDefault(m => !before.Contains(MonitorKey(m)) &&
            m.Region.Right - m.Region.Left == ResolveVirtualWidth(display) &&
            m.Region.Bottom - m.Region.Top == ResolveVirtualHeight(display));

    /// <summary>Every monitor currently published by a virtual display adapter, tolerant of a transient failure.</summary>
    private static List<WindowsMonitor> VirtualMonitors()
    {
        try { return [.. WindowsMonitorTopology.Enumerate().Where(VirtualDisplayDrivers.IsVirtualMonitor)]; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return []; }
    }

    private static HashSet<string> VirtualMonitorKeys() =>
        [.. VirtualMonitors().Select(MonitorKey)];

    internal static string MonitorKey(WindowsMonitor monitor) => MonitorKey(monitor.DeviceName, monitor.HardwareId);

    internal static string MonitorKey(string deviceName, string? hardwareId) =>
        $"dev:{deviceName}";

    /// <summary>Waits for the SudoVDA adapter to appear and returns its GDI device name.</summary>
    private static string ResolveAdapterDeviceName(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + AttachTimeout;
        var delay = TimeSpan.FromMilliseconds(20);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WindowsMonitorTopology.FindAdapterDeviceName(SudoVdaDriver.AdapterHardwareId) is { } name)
                return name;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The SudoVDA display adapter is not present in the desktop.");
            Thread.Sleep(delay);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 320));
        }
    }

    /// <summary>Converts the remote panel's reported refresh rate to the whole Hertz the driver accepts.</summary>
    internal static int ResolveRefreshHertz(int? refreshMilliHertz) =>
        refreshMilliHertz is > 0 ? Math.Clamp(refreshMilliHertz.Value / 1000, 1, 500) : SudoVdaDriver.DefaultRefreshHertz;

    /// <summary>
    /// The Windows virtual panel mirrors the remote output's physical mode, oriented as the user sees it.
    /// Input coordinates are separately scaled from that panel rectangle into the compositor's logical size.
    /// Metadata from older peers may omit the native mode, in which case WidthPixels remains the fallback.
    /// </summary>
    internal static int ResolveVirtualWidth(DisplayMetadata display) =>
        display.NativeWidthPixels is > 0 && display.NativeHeightPixels is > 0
            ? display.RotationDegrees is 90 or 270 ? display.NativeHeightPixels.Value : display.NativeWidthPixels.Value
            : display.WidthPixels;

    internal static int ResolveVirtualHeight(DisplayMetadata display) =>
        display.NativeWidthPixels is > 0 && display.NativeHeightPixels is > 0
            ? display.RotationDegrees is 90 or 270 ? display.NativeWidthPixels.Value : display.NativeHeightPixels.Value
            : display.HeightPixels;
}

/// <summary>Derives the driver monitor identity that Windows remembers display layouts against.</summary>
internal static class MonitorIdentity
{
    /// <summary>
    /// A GUID per remote display <em>and mode</em>. SudoVDA caches a monitor's mode against its GUID: asking
    /// for the same GUID again returns the panel at its original size, so a resolution change can only take
    /// effect under a new identity. Folding the mode in gives both behaviours that matter: a restart at the
    /// same mode reuses the identity so Windows restores the previous arrangement, while a mode change
    /// produces a new panel at the new size.
    /// </summary>
    public static Guid For(string remoteDisplayId, int widthPixels, int heightPixels, int refreshHertz)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{remoteDisplayId}\u0000{widthPixels}x{heightPixels}@{refreshHertz}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

