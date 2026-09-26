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
    private CancellationTokenSource? _keepAlive;
    private Task? _keepAliveLoop;

    /// <summary>
    /// Monitors this daemon created, persisted so a later run can reclaim them. SudoVDA has no request that
    /// lists monitors, so without a record a monitor left behind by a killed process is unrecoverable and the
    /// desktop accumulates one per crash. Only identities this daemon issued are ever removed.
    /// </summary>
    private static string StatePath => Path.Combine(AppPaths.Root, "virtual-display.json");

    private sealed record AttachmentRecord(Guid MonitorId, string HardwareId, string DeviceName);

    public bool IsAvailable => OperatingSystem.IsWindows() && DriverPresent.Value;

    public async ValueTask<VirtualDisplayAttachment> AttachAsync(DisplayMetadata display,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (!IsAvailable)
            throw new PlatformNotSupportedException(
                "No virtual display driver is installed. Install SudoVDA, then restart the daemon.");
        if (display.WidthPixels is < 1 or > SudoVdaDriver.MaxWidthPixels ||
            display.HeightPixels is < 1 or > SudoVdaDriver.MaxHeightPixels)
            throw new ArgumentOutOfRangeException(nameof(display),
                "The remote display mode is outside the range SudoVDA can present.");

        var refreshHertz = ResolveRefreshHertz(display.RefreshMilliHertz);
        var monitorId = MonitorIdentity.For(display.Id, display.WidthPixels, display.HeightPixels, refreshHertz);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // SudoVDA has no in-place reconfigure, so a mode change is a remove followed by an add. The
            // driver tears a monitor down asynchronously, so remember which panels we are about to drop and
            // wait for those specific ones to leave the desktop before asking for another. A monitor owned
            // by another application is left alone rather than waited on.
            var departing = VirtualMonitorIds();
            await DetachCoreAsync().ConfigureAwait(false);
            await WaitForRemovalAsync(departing, cancellationToken).ConfigureAwait(false);
            var device = _device ??= SudoVdaDriver.Open() ?? throw new PlatformNotSupportedException(
                "The SudoVDA control device disappeared; restart the Windows session.");
            ResolveAdapterDeviceName(cancellationToken);
            ReclaimAbandoned(device);

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
                    $"Windows did not publish a {display.WidthPixels}x{display.HeightPixels} virtual monitor.");
            monitorId = created.MonitorId;
            Remember(created);
            StartKeepAlive();
            log?.Invoke($"Virtual display {created.DeviceName} active at " +
                $"{display.WidthPixels}x{display.HeightPixels}@{refreshHertz}.");
            return created with { RefreshHertz = refreshHertz };
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes monitors a previous run of this daemon created but never got to detach.</summary>
    private void ReclaimAbandoned(SafeFileHandle device)
    {
        var records = ReadRecords();
        if (records.Count == 0) return;
        var kept = new List<AttachmentRecord>();
        foreach (var record in records)
        {
            if (!WindowsMonitorTopology.Exists(record.HardwareId)) continue; // Already gone; drop the record.
            try
            {
                SudoVdaDriver.Remove(device, record.MonitorId);
                log?.Invoke($"Removed a virtual display abandoned by an earlier run ({record.DeviceName}).");
            }
            catch (Win32Exception)
            {
                // Keep the record so a later run can try again rather than leaking the monitor for good.
                kept.Add(record);
            }
        }
        WriteRecords(kept);
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

    private static void Remember(VirtualDisplayAttachment attachment) =>
        WriteRecords([new(attachment.MonitorId, attachment.MonitorHardwareId, attachment.DeviceName)]);

    private static void Forget(Guid monitorId) =>
        WriteRecords([.. ReadRecords().Where(record => record.MonitorId != monitorId)]);

    /// <summary>Adds a monitor under one identity, returning null if Windows never publishes it.</summary>
    private async Task<VirtualDisplayAttachment?> CreateAsync(SafeFileHandle device, DisplayMetadata display,
        int refreshHertz, Guid monitorId, CancellationToken cancellationToken)
    {
        var before = VirtualMonitorIds();
        try
        {
            SudoVdaDriver.Add(device, monitorId, display.WidthPixels, display.HeightPixels,
                refreshHertz, MonitorDeviceName);
        }
        catch (Win32Exception) when (TryAdoptStale(display))
        {
            // A hard-killed daemon can leave its monitor behind, and the driver has no list-monitors
            // request, so its identity cannot be recovered. Reuse a matching monitor rather than failing.
            log?.Invoke($"Reused an existing {display.WidthPixels}x{display.HeightPixels} virtual display.");
            var adopted = VirtualMonitors().FirstOrDefault(m =>
                m.Region.Right - m.Region.Left == display.WidthPixels &&
                m.Region.Bottom - m.Region.Top == display.HeightPixels);
            return adopted is null ? null : new(adopted.DeviceName, adopted.HardwareId, monitorId,
                display.WidthPixels, display.HeightPixels, refreshHertz);
        }
        _ownedMonitorId = monitorId;
        var published = await TryWaitForMonitorAsync(before, display, cancellationToken).ConfigureAwait(false);
        if (published is not null)
            return new(published.DeviceName, published.HardwareId, monitorId,
                display.WidthPixels, display.HeightPixels, refreshHertz);
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
                Forget(monitorId);
            }
            catch (Win32Exception ex) { log?.Invoke($"Virtual display removal failed: {ex.Message}"); }
        }
        _ownedMonitorId = null;
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
            }
        });
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
            var present = VirtualMonitorIds();
            if (!departing.Overlaps(present) || DateTime.UtcNow >= deadline) return;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Finds a monitor on the virtual adapter that was not present before the request.</summary>
    private static WindowsMonitor? FindNewMonitor(HashSet<string> before, DisplayMetadata display) =>
        VirtualMonitors().FirstOrDefault(m => !before.Contains(m.HardwareId) &&
            m.Region.Right - m.Region.Left == display.WidthPixels &&
            m.Region.Bottom - m.Region.Top == display.HeightPixels);

    /// <summary>Reports whether a matching virtual monitor already exists, used to recover a stale attach.</summary>
    private static bool TryAdoptStale(DisplayMetadata display) =>
        VirtualMonitors().Any(m => m.Region.Right - m.Region.Left == display.WidthPixels &&
            m.Region.Bottom - m.Region.Top == display.HeightPixels);

    /// <summary>Every monitor currently published by a virtual display adapter, tolerant of a transient failure.</summary>
    private static List<WindowsMonitor> VirtualMonitors()
    {
        try { return [.. WindowsMonitorTopology.Enumerate().Where(VirtualDisplayDrivers.IsVirtualMonitor)]; }
        catch (InvalidOperationException) { return []; }
    }

    private static HashSet<string> VirtualMonitorIds() =>
        [.. VirtualMonitors().Select(m => m.HardwareId)];

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
            $"{remoteDisplayId} {widthPixels}x{heightPixels}@{refreshHertz}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

