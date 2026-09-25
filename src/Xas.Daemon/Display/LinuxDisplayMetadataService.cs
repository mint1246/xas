using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Xas.Core;

namespace Xas.Daemon.Display;

/// <summary>
/// Reads display state from the logged-in Linux desktop session. X11 uses xrandr; Wayland uses
/// wlr-randr (wlroots compositors) or kscreen-doctor (KDE). A user-session process is required
/// because these commands need the desktop's session environment and compositor connection.
/// Missing facts are left null; this service never substitutes guessed dimensions or rates.
/// </summary>
public sealed class LinuxDisplayMetadataService
{
    private readonly Func<string, CancellationToken, Task<string?>> _run;

    public LinuxDisplayMetadataService() : this(RunCommandAsync) { }

    internal LinuxDisplayMetadataService(Func<string, CancellationToken, Task<string?>> run) => _run = run;

    public bool IsAvailable => OperatingSystem.IsLinux() &&
        (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            ? FindExecutable("wlr-randr") is not null || FindExecutable("kscreen-doctor") is not null
            : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) &&
                FindExecutable("xrandr") is not null);

    public async Task<DisplayMetadata?> GetPrimaryDisplayAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var wayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (wayland)
        {
            foreach (var command in new[] { "wlr-randr", "kscreen-doctor" })
            {
                var output = await _run(command, cancellationToken).ConfigureAwait(false);
                var parsed = command == "wlr-randr" ? ParseWlrRandr(output) : ParseKScreenDoctor(output);
                if (parsed is not null) return parsed;
            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        return ParseXrandr(await _run("xrandr", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Parses xrandr --query output and selects its declared primary, then an eDP/LVDS panel.</summary>
    public static DisplayMetadata? ParseXrandr(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var displays = new List<(DisplayMetadata Data, bool Primary, bool BuiltIn)>();
        string? name = null; bool primary = false; int physicalWidth = 0, physicalHeight = 0, rotation = 0;
        foreach (var line in text.Split('\n'))
        {
            var output = Regex.Match(line, @"^([A-Za-z0-9_.-]+) connected(?: (primary))?(.*)$");
            if (output.Success)
            {
                name = output.Groups[1].Value;
                primary = output.Groups[2].Success;
                var physical = Regex.Match(output.Groups[3].Value, @"(\d+)mm x (\d+)mm");
                physicalWidth = physical.Success ? ParsePositive(physical.Groups[1].Value) : 0;
                physicalHeight = physical.Success ? ParsePositive(physical.Groups[2].Value) : 0;
                rotation = RotationFromText(line);
                continue;
            }
            if (name is null) continue;
            var mode = Regex.Match(line, @"^\s+(\d+)x(\d+)\s+(.+)$");
            if (!mode.Success) continue;
            var active = Regex.Match(mode.Groups[3].Value, @"(?<![\d.])(\d+(?:\.\d+)?)\*");
            if (!active.Success) continue;
            var builtIn = name.StartsWith("eDP", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LVDS", StringComparison.OrdinalIgnoreCase);
            displays.Add((Make(name, int.Parse(mode.Groups[1].Value), int.Parse(mode.Groups[2].Value), HZ(active.Groups[1].Value), physicalWidth, physicalHeight, rotation, null), primary, builtIn));
            name = null;
        }
        return Choose(displays);
    }

    /// <summary>Parses wlr-randr output, including its current mode, transform, scale and focused output.</summary>
    public static DisplayMetadata? ParseWlrRandr(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var displays = new List<(DisplayMetadata Data, bool Primary, bool BuiltIn)>();
        string? name = null; bool focused = false, enabled = false; int pw = 0, ph = 0, rotation = 0, modeWidth = 0, modeHeight = 0; int? refresh = null; double? scale = null;
        foreach (var line in text.Split('\n'))
        {
            if (!char.IsWhiteSpace(line.FirstOrDefault()) && !string.IsNullOrWhiteSpace(line))
            {
                Flush();
                var head = Regex.Match(line.Trim(), @"^([^\s]+)(?:\s+.*)?(?:\s+\(focused\))?$");
                name = head.Success ? head.Groups[1].Value : null;
                focused = line.Contains("(focused)", StringComparison.OrdinalIgnoreCase);
                enabled = false; pw = ph = rotation = modeWidth = modeHeight = 0; refresh = null; scale = null;
                continue;
            }
            if (name is null) continue;
            var t = line.Trim();
            enabled |= t.Equals("Enabled: yes", StringComparison.OrdinalIgnoreCase);
            var size = Regex.Match(t, @"^Physical size:\s*(\d+)\s*x\s*(\d+)\s*mm", RegexOptions.IgnoreCase);
            if (size.Success) { pw = ParsePositive(size.Groups[1].Value); ph = ParsePositive(size.Groups[2].Value); }
            var sc = Regex.Match(t, @"^Scale:\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (sc.Success && double.TryParse(sc.Groups[1].Value, CultureInfo.InvariantCulture, out var s) && s > 0) scale = s;
            var tr = Regex.Match(t, @"^Transform:\s*(\S+)", RegexOptions.IgnoreCase);
            if (tr.Success) rotation = RotationFromTransform(tr.Groups[1].Value);
            var mode = Regex.Match(t, @"^(\d+)x(\d+) px,\s*(\d+(?:\.\d+)?) Hz\s*\(current\)", RegexOptions.IgnoreCase);
            if (mode.Success)
            {
                modeWidth = int.Parse(mode.Groups[1].Value);
                modeHeight = int.Parse(mode.Groups[2].Value);
                refresh = HZ(mode.Groups[3].Value);
            }
        }
        Flush();
        return Choose(displays);

        void Flush()
        {
            if (name is null || !enabled || modeWidth <= 0 || modeHeight <= 0) return;
            var builtIn = name.StartsWith("eDP", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LVDS", StringComparison.OrdinalIgnoreCase);
            displays.Add((Make(name, modeWidth, modeHeight, refresh, pw, ph, rotation, scale), focused, builtIn));
        }
    }

    /// <summary>Best-effort parser for KDE's documented kscreen-doctor -o human-readable output.</summary>
    public static DisplayMetadata? ParseKScreenDoctor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var blocks = Regex.Split(text, @"(?=^Output:\s*\d+\s)", RegexOptions.Multiline);
        var displays = new List<(DisplayMetadata Data, bool Primary, bool BuiltIn)>();
        foreach (var block in blocks)
        {
            var head = Regex.Match(block, @"^Output:\s*\d+\s+(\S+)(.*)$", RegexOptions.Multiline);
            var geom = Regex.Match(block, @"^\s*Geometry:\s*-?\d+,-?\d+\s+(\d+)x(\d+)", RegexOptions.Multiline);
            if (!head.Success || !geom.Success) continue;
            var name = head.Groups[1].Value;
            var modes = Regex.Match(block, @"^\s*Modes:\s*.*?(\d+)x(\d+)@(\d+(?:\.\d+)?)(?:\*|!)", RegexOptions.Multiline);
            int width = modes.Success ? int.Parse(modes.Groups[1].Value) : int.Parse(geom.Groups[1].Value);
            int height = modes.Success ? int.Parse(modes.Groups[2].Value) : int.Parse(geom.Groups[2].Value);
            int? refresh = modes.Success ? HZ(modes.Groups[3].Value) : null;
            var physical = Regex.Match(block, @"^\s*Physical size:\s*(\d+)x(\d+)", RegexOptions.Multiline);
            var rot = Regex.Match(block, @"^\s*Rotation:\s*(\d+)", RegexOptions.Multiline);
            var sc = Regex.Match(block, @"^\s*Scale:\s*(\d+(?:\.\d+)?)", RegexOptions.Multiline);
            double? scale = sc.Success && double.TryParse(sc.Groups[1].Value, CultureInfo.InvariantCulture, out var scaleVal) ? scaleVal : null;
            var rotation = rot.Success ? KScreenRotation(int.Parse(rot.Groups[1].Value)) : 0;
            var priority = Regex.Match(head.Groups[2].Value, @"\bpriority\s+(\d+)", RegexOptions.IgnoreCase);
            displays.Add((Make(name, width, height, refresh, physical.Success ? ParsePositive(physical.Groups[1].Value) : 0, physical.Success ? ParsePositive(physical.Groups[2].Value) : 0,
                rotation, scale), priority.Success && priority.Groups[1].Value == "1", name.StartsWith("eDP", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LVDS", StringComparison.OrdinalIgnoreCase)));
        }
        return Choose(displays);
    }

    private static DisplayMetadata? Choose(List<(DisplayMetadata Data, bool Primary, bool BuiltIn)> items) => items
        .OrderByDescending(x => x.Primary).ThenByDescending(x => x.BuiltIn).Select(x => x.Data).FirstOrDefault();
    private static DisplayMetadata Make(string id, int w, int h, int? hz, int pw, int ph, int rotation, double? scale) => new()
    {
        Id = id, Name = id, WidthPixels = rotation is 90 or 270 ? h : w, HeightPixels = rotation is 90 or 270 ? w : h,
        RefreshMilliHertz = hz, PhysicalWidthMillimeters = pw > 0 ? pw : null, PhysicalHeightMillimeters = ph > 0 ? ph : null,
        RotationDegrees = rotation, Scale = scale
    };
    private static int? HZ(string text) => double.TryParse(text, CultureInfo.InvariantCulture, out var hz) && hz > 0 ? (int)Math.Round(hz * 1000) : null;
    private static int ParsePositive(string text) => int.TryParse(text, out var x) && x > 0 ? x : 0;
    private static int RotationFromText(string line) => Regex.Match(line, @"\((normal|left|inverted|right)\s", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant() switch
    { "left" => 90, "inverted" => 180, "right" => 270, _ => 0 };
    private static int RotationFromTransform(string text) => text.ToLowerInvariant() switch { "90" or "90_flipped" => 90, "180" or "180_flipped" => 180, "270" or "270_flipped" => 270, _ => 0 };
    private static int KScreenRotation(int value) => value switch { 2 or 32 => 90, 4 or 64 => 180, 8 or 128 => 270, _ => 0 };

    private static async Task<string?> RunCommandAsync(string command, CancellationToken cancellationToken)
    {
        var executable = FindExecutable(command);
        if (executable is null) return null;
        Process? process = null;
        try
        {
            var arguments = command switch { "kscreen-doctor" => "-o", "xrandr" => "--query", _ => "" };
            process = Process.Start(new ProcessStartInfo(executable, arguments)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false, CreateNoWindow = true });
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var result = await output.ConfigureAwait(false);
            return process.ExitCode == 0 ? result : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                process.Dispose();
            }
        }
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
    }
}
