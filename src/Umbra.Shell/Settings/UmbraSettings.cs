using System.Globalization;
using System.IO;
using Umbra.Core.Layout;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Settings;

/// <summary>
/// Umbra's configuration, as stored on disk.
/// </summary>
/// <remarks>
/// <para>
/// Plain <c>key = value</c> text, for the same reason the pin list is: a taskbar layout is
/// something people want to inspect, back up and edit by hand, and a binary or registry-backed
/// format buys nothing here. Unknown keys are dropped on save rather than preserved — the file is
/// small and rewriting it whole keeps the reader trivial.
/// </para>
/// <para>
/// <b>Defaults are the spec's, with one deliberate exception.</b> §7.1 makes bottom edge with a
/// left-aligned row the shipping default. The dormant icon extent does <i>not</i> default to
/// §7.1's 24 px — see <see cref="DormantIconSize"/> for why the bar ships with idle icons shrunk.
/// </para>
/// <para>
/// Command-line flags override the file where both are given. The flags exist for experiments
/// (<c>--no-motion</c> is there so the §11.2 Q1 comparison needs no rebuild), and an experiment
/// that silently lost to a stored setting would be worse than useless.
/// </para>
/// </remarks>
internal sealed class UmbraSettings
{
    /// <summary>Where the settings live.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Umbra",
        "settings.txt");

    /// <summary>Which screen edge the bar occupies (§7.1 default: bottom).</summary>
    public BarEdge Edge { get; set; } = BarEdge.Bottom;

    /// <summary>Where the icon row sits along the long axis (§7.1 default: start).</summary>
    public Alignment Alignment { get; set; } = Alignment.Start;

    /// <summary>Full icon extent, and the size drawn once the bar is engaged (§7.1).</summary>
    public const double FullIconSize = SlotMetrics.BaseIconExtent;

    /// <summary>
    /// Icon extent while the bar is dormant, at 100% scaling. Grows to §7.1's 24 px on hover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Defaults to 16, not to §7.1's 24.</b> This is a deliberate deviation, taken by the
    /// project owner: the bar ships with idle icons shrunk and growing back on approach, rather
    /// than shipping at full size and leaving the shrink as something to find. The fidelity cost is
    /// paid only while the bar is dormant and the pointer is elsewhere, and the icon is at the
    /// Windows 11 size for the whole of the time anyone is actually looking at it.
    /// </para>
    /// <para>
    /// 16 rather than a smaller number because <see cref="IconSizeRamp"/> shows the payoff curve is
    /// brutal: the permanently-lit core vanishes at 19.29 px and buys <i>nothing</i> there, 16 px is
    /// the first size where the hottest pixel actually cools (1.25×), and going further trades
    /// legibility fast — 12 px reaches 2.32× but no longer reads as a taskbar.
    /// </para>
    /// </remarks>
    public double DormantIconSize { get; set; } = 16.0;

    /// <summary>Whether the motion layer runs (§6).</summary>
    public bool Motion { get; set; } = true;

    /// <summary>
    /// Whether to host the notification area.
    /// </summary>
    /// <remarks>
    /// Off by default. The interception works but Windows 11 25H2 does not deliver icons that way,
    /// so it collects nothing while costing a permanent 100 ms timer — docs/measurements.md step 7.
    /// </remarks>
    public bool Tray { get; set; }

    /// <summary>
    /// Device name of the monitor to occupy, or empty for the primary.
    /// </summary>
    /// <remarks>
    /// Stored as the device name (<c>\\.\DISPLAY1</c>) rather than an index, because indices are
    /// reassigned when a monitor is unplugged and the bar would silently move to a different
    /// screen. A name that no longer matches any attached screen falls back to the primary.
    /// </remarks>
    public string Monitor { get; set; } = string.Empty;

    /// <summary>Reads the settings, falling back to the defaults for anything missing or unusable.</summary>
    public static UmbraSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        UmbraSettings settings = new();

        try
        {
            if (!File.Exists(path)) return settings;

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                int split = trimmed.IndexOf('=');
                if (split <= 0) continue;

                string key = trimmed[..split].Trim();
                string value = trimmed[(split + 1)..].Trim();

                settings.Apply(key, value);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            BringUpLog.Write(e, "settings load failed; using defaults");
        }

        return settings;
    }

    /// <summary>Applies one key/value pair. Unusable values leave the default in place.</summary>
    private void Apply(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "edge":
                if (Enum.TryParse(value, ignoreCase: true, out BarEdge edge)) Edge = edge;
                else Unusable(key, value);
                break;

            case "alignment":
                if (Enum.TryParse(value, ignoreCase: true, out Alignment alignment)) Alignment = alignment;
                else Unusable(key, value);
                break;

            case "dormanticonsize":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double size)
                    && size > 0 && size <= SlotMetrics.BasePitch)
                {
                    DormantIconSize = size;
                }
                else Unusable(key, value);
                break;

            case "motion":
                if (bool.TryParse(value, out bool motion)) Motion = motion;
                else Unusable(key, value);
                break;

            case "tray":
                if (bool.TryParse(value, out bool tray)) Tray = tray;
                else Unusable(key, value);
                break;

            case "monitor":
                Monitor = value;
                break;

            default:
                BringUpLog.Write($"settings: ignoring unknown key '{key}'");
                break;
        }
    }

    private static void Unusable(string key, string value) =>
        BringUpLog.Write($"settings: unusable value for '{key}' ('{value}'), keeping the default");

    /// <summary>Writes the settings.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, Serialise());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            BringUpLog.Write(e, "settings save failed");
        }
    }

    /// <summary>The file's lines. Separated from <see cref="Save"/> so it can be tested without disk.</summary>
    public IReadOnlyList<string> Serialise() =>
    [
        "# Umbra settings. Edit by hand if you like; Umbra rewrites this file when you change",
        "# anything in its settings window, and drops comments and unknown keys when it does.",
        "",
        "# left | top | right | bottom",
        $"edge = {Edge}",
        "",
        "# start | center",
        $"alignment = {Alignment}",
        "",
        "# Icon extent while dormant, at 100% scaling. Grows back to 24 on hover.",
        "# 20 buys nothing; 16 is the first size where burn-in actually improves.",
        string.Create(CultureInfo.InvariantCulture, $"dormantIconSize = {DormantIconSize}"),
        "",
        "# The drift and micro-jitter layer.",
        $"motion = {Motion.ToString().ToLowerInvariant()}",
        "",
        "# Notification area hosting. Collects nothing on Windows 11 25H2 and costs a 100 ms timer.",
        $"tray = {Tray.ToString().ToLowerInvariant()}",
        "",
        @"# Device name of the monitor to occupy, e.g. \\.\DISPLAY1. Empty means the primary.",
        $"monitor = {Monitor}",
    ];

    /// <summary>A copy, so a settings window can be cancelled without having mutated anything.</summary>
    public UmbraSettings Clone() => new()
    {
        Edge = Edge,
        Alignment = Alignment,
        DormantIconSize = DormantIconSize,
        Motion = Motion,
        Tray = Tray,
        Monitor = Monitor,
    };

    /// <summary>
    /// Whether moving to <paramref name="other"/> needs the bar restarted rather than just redrawn.
    /// </summary>
    /// <remarks>
    /// The edge, the alignment and the monitor are all baked into the AppBar registration and the
    /// window's geometry at construction. §6.3 forbids re-registering the reserved rectangle during
    /// steady-state operation because it reflows every maximised window on the desktop — an
    /// explicit settings change is exactly the exception that rule allows for, but doing it in
    /// place is far more work than restarting, and restarting is honest about what it costs.
    /// </remarks>
    public bool NeedsRestartToReach(UmbraSettings other) => Tray != other.Tray;
}
