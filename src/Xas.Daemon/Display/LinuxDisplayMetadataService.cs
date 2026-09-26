using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Xas.Core;

namespace Xas.Daemon.Display;

/// <summary>
/// Reads display state from the logged-in Linux desktop session. X11 uses xrandr; Wayland uses wlr-randr
/// (wlroots compositors), kscreen-doctor (KDE), or Mutter's DisplayConfig D-Bus API (GNOME), which gdbus
/// reaches directly. A user-session process is required because these commands need the desktop's session
/// environment and compositor connection. Missing facts are left null; this service never substitutes
/// guessed dimensions or rates.
/// </summary>
public sealed class LinuxDisplayMetadataService
{
    private readonly Func<string, CancellationToken, Task<string?>> _run;

    public LinuxDisplayMetadataService() : this(RunCommandAsync) { }

    internal LinuxDisplayMetadataService(Func<string, CancellationToken, Task<string?>> run) => _run = run;

    public bool IsAvailable => OperatingSystem.IsLinux() && Backends.Any(backend => FindExecutable(backend) is not null);

    /// <summary>
    /// Session backends in preference order. A Wayland compositor only reports outputs to its own clients, so
    /// each desktop has its own tool and none of them work on the others: wlroots and Mutter both ignore
    /// xrandr under Wayland, because all a client sees is XWayland's merged view.
    /// </summary>
    private static readonly string[] WaylandBackends = ["wlr-randr", "kscreen-doctor", "gdbus"];

    private static string[] Backends => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        ? WaylandBackends
        : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) ? ["xrandr"] : [];

    public async Task<DisplayMetadata?> GetPrimaryDisplayAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return null;
        foreach (var backend in Backends)
        {
            var output = await _run(backend, cancellationToken).ConfigureAwait(false);
            var parsed = backend switch
            {
                "wlr-randr" => ParseWlrRandr(output),
                "kscreen-doctor" => ParseKScreenDoctor(output),
                "gdbus" => ParseMutterState(output),
                _ => ParseXrandr(output)
            };
            if (parsed is not null) return parsed;
        }
        return null;
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

    /// <summary>
    /// Parses the reply of Mutter's org.gnome.Mutter.DisplayConfig.GetCurrentState, the API GNOME Settings
    /// itself uses. gdbus prints GVariant text, so the reply is walked with balanced-delimiter splitting
    /// rather than a single pattern: the nesting is irregular enough that one regex would be fragile.
    /// </summary>
    public static DisplayMetadata? ParseMutterState(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var reply = Unwrap(text);
        if (reply is null) return null;
        // (serial, monitors, logical_monitors, properties)
        var fields = SplitTopLevel(reply, ',');
        if (fields.Count < 3) return null;
        var layoutLogical = Regex.Match(fields[3] ?? string.Empty, @"'layout-mode':\s*<\s*uint32\s+(\d+)").Groups[1].Value == "1";

        // Each monitor is ((connector, vendor, product, serial), [modes], {properties}).
        var displays = new List<(DisplayMetadata Data, bool Primary, bool BuiltIn)>();
        var byConnector = new Dictionary<string, (int Width, int Height, int? MilliHertz)>(StringComparer.Ordinal);
        foreach (var monitor in SplitElements(fields[1]))
        {
            var parts = SplitTopLevel(monitor, ',');
            if (parts.Count < 3) continue;
            var connector = FirstString(parts[0]);
            if (connector.Length == 0) continue;
            var properties = parts[2];
            foreach (var candidate in SplitElements(parts[1]))
            {
                var fieldsOfMode = SplitTopLevel(candidate, ',');
                if (fieldsOfMode.Count < 4) continue;
                // Only the mode Mutter marks current describes the live desktop.
                if (!fieldsOfMode[6].Contains("'is-current': <true>", StringComparison.Ordinal)) continue;
                if (!int.TryParse(fieldsOfMode[1], out var width) ||
                    !int.TryParse(fieldsOfMode[2], out var height)) break;
                if (width <= 0 || height <= 0) break;
                byConnector[connector] = (width, height, MilliHertz(fieldsOfMode[3]));
                break;
            }
            var physicalWidth = PropertyInt(properties, "width-mm");
            var physicalHeight = PropertyInt(properties, "height-mm");
            // Mutter lists every plugged output, including ones the user switched off in Settings, so a
            // connector only counts as the desktop when a logical monitor actually drives it.
            var logical = SplitElements(fields[2])
                .Select(entry => SplitTopLevel(entry, ',')).FirstOrDefault(parts => parts.Count >= 6 &&
                    parts[5].Contains($"'{connector}'", StringComparison.Ordinal));
            if (logical is null || !byConnector.TryGetValue(connector, out var mode) || mode.Width <= 0) continue;
            var scale = 1.0;
            if (double.TryParse(logical[2], NumberStyles.Float, CultureInfo.InvariantCulture,
                out var parsedScale) && parsedScale > 0) scale = parsedScale;
            // gdbus annotates the transform as "uint32 1"; a bare digit match would read the 32.
            var transform = int.TryParse(Regex.Match(logical[3].Trim(), @"^(?:u?int32\s+)?(\d+)$")
                .Groups[1].Value, out var parsedTransform) ? parsedTransform : 0;
            var primary = logical[5].TrimStart().StartsWith("true", StringComparison.Ordinal);
            // Under logical layout the desktop the user works in is the mode divided by the scale, and that is
            // the coordinate space absolute input has to be expressed in. Under physical layout it is the mode.
            var logicalWidth = layoutLogical ? Math.Max(1, (int)Math.Round(mode.Width / scale)) : mode.Width;
            var logicalHeight = layoutLogical ? Math.Max(1, (int)Math.Round(mode.Height / scale)) : mode.Height;
            var rotation = MutterRotation(transform);
            var displayName = PropertyString(properties, "display-name");
            displays.Add((new DisplayMetadata
            {
                Id = connector,
                Name = displayName.Length > 0 ? displayName : connector,
                WidthPixels = rotation is 90 or 270 ? logicalHeight : logicalWidth,
                HeightPixels = rotation is 90 or 270 ? logicalWidth : logicalHeight,
                RefreshMilliHertz = mode.MilliHertz,
                PhysicalWidthMillimeters = physicalWidth > 0 ? physicalWidth : null,
                PhysicalHeightMillimeters = physicalHeight > 0 ? physicalHeight : null,
                RotationDegrees = rotation,
                Scale = scale == 1.0 ? null : scale
            }, primary, properties.Contains("'is-builtin': <true>", StringComparison.Ordinal)));
        }
        return Choose(displays);
    }

    /// <summary>Strips the GVariant annotations and outer parentheses gdbus wraps a reply in.</summary>
    private static string? Unwrap(string text)
    {
        var value = text.Trim();
        foreach (var annotation in new[] { "@a{sv} ", "uint32 ", "int32 " })
            if (value.StartsWith(annotation, StringComparison.Ordinal))
                value = value[annotation.Length..].Trim();
        return value.Length > 1 && value[0] == '(' && value[^1] == ')' ? value[1..^1] : null;
    }

    /// <summary>
    /// Splits on a delimiter that is not nested inside brackets or quotes. gdbus output mixes (), [], {} and
    /// quoted strings, so a plain Split would cut tuples in half.
    /// </summary>
    private static List<string> SplitTopLevel(string text, char delimiter)
    {
        var parts = new List<string>();
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'') quoted = !quoted;
            else if (!quoted && c is '(' or '[' or '{') depth++;
            else if (!quoted && c is ')' or ']' or '}') depth--;
            else if (!quoted && depth == 0 && c == delimiter)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(text[start..].Trim());
        return parts;
    }

    private static List<string> SplitTopLevel(string text, char open, char close) =>
        text.Length > 1 && text[0] == open && text[^1] == close
            ? SplitTopLevel(text[1..^1], ',')
            : [];

    /// <summary>
    /// Returns the elements of a bracketed array whose entries are parenthesised tuples, with each element's
    /// own parentheses removed. Splitting on commas would tear the tuples apart instead.
    /// </summary>
    private static List<string> SplitElements(string array)
    {
        var items = new List<string>();
        if (array.Length < 2 || array[0] != '[' || array[^1] != ']') return items;
        var depth = 0;
        var quoted = false;
        var start = -1;
        for (var i = 1; i < array.Length - 1; i++)
        {
            var c = array[i];
            if (c == '\'') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '(')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    items.Add(array[(start + 1)..i]);
                    start = -1;
                }
            }
        }
        return items;
    }

    private static string FirstString(string text)
    {
        var match = Regex.Match(text, @"'((?:[^'\\]|\\.)*)'");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static int? MilliHertz(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) && hz > 0
            ? (int)Math.Round(hz * 1000) : null;

    private static int PropertyInt(string properties, string key) =>
        int.TryParse(Regex.Match(properties, $"'{key}':\\s*<\\s*(?:int32\\s+)?(-?\\d+)").Groups[1].Value,
            out var value) && value > 0 ? value : 0;

    private static string PropertyString(string properties, string key)
    {
        var match = Regex.Match(properties, $"'{key}':\\s*<\\s*'((?:[^'\\\\]|\\\\.)*)'");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>Mutter transform codes: 0 normal, 1 90, 2 180, 3 270, 4-7 the same rotations flipped.</summary>
    private static int MutterRotation(int transform) => transform switch { 1 or 5 => 90, 2 or 6 => 180, 3 or 7 => 270, _ => 0 };

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
            var arguments = command switch
            {
                "kscreen-doctor" => "-o",
                "xrandr" => "--query",
                // Mutter's own display API, the same one GNOME Settings uses. gdbus ships with GLib, so this
                // needs no extra package on any GNOME install.
                "gdbus" => "call --session --dest org.gnome.Mutter.DisplayConfig " +
                    "--object-path /org/gnome/Mutter/DisplayConfig " +
                    "--method org.gnome.Mutter.DisplayConfig.GetCurrentState",
                _ => ""
            };
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

