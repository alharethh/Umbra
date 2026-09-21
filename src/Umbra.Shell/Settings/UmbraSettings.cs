using System.Globalization;
using System.IO;
using Umbra.Core.Layout;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Settings;

/// <summary>What an application icon does when the pointer is on it.</summary>
internal enum AppIconHover
{
    /// <summary>The plain rounded chip the row has always had (§3.2).</summary>
    Chip,

    /// <summary>The holographic pane the notification area uses.</summary>
    Holographic,
}


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
    /// Whether a slot shows its application's icon rather than its current window's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A deviation from Windows 11, recorded in design-spec §8.</b> File Explorer answers
    /// <c>WM_GETICON</c> with the icon of the folder it is showing — measured, two windows on
    /// Downloads and Videos return different icons — and since mid-2025 Windows 11 draws exactly
    /// that on the taskbar button, with no setting to stop it. A slot that groups several windows
    /// then has to pick one, and the icon changes as windows come and go.
    /// </para>
    /// <para>
    /// On by default: a taskbar exists so applications are where you expect them, and an icon that
    /// changes while you work undermines that for no benefit. Off restores Windows' behaviour.
    /// </para>
    /// </remarks>
    public bool ApplicationIcons { get; set; } = true;

    /// <summary>
    /// Whether to host the notification area.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On by default</b> — the project owner's decision, 2026-09-20. It was off because tray
    /// hosting costs a permanent 100 ms timer and §11.2 Q2, whether ManagedShell's timers defeat
    /// display idle, is unmeasured. <b>Q2 is still unmeasured and this does not settle it</b>; what
    /// settled the default is that a taskbar replacement without a notification area is not one.
    /// </para>
    /// <para>
    /// <b>Changing this does not reach anyone who has run Umbra before.</b> The file is rewritten
    /// with every key whenever settings are saved, so an existing <c>settings.txt</c> carries the
    /// old <c>tray = false</c> explicitly and a stored setting rightly beats a default. That is
    /// working as intended, and it is also how this went unnoticed: the flag and the setting were
    /// both true in testing while the file on disk said otherwise.
    /// </para>
    /// </remarks>
    public bool Tray { get; set; } = true;

    /// <summary>
    /// Colour of the holographic hover animation on the notification area and the trailing
    /// controls.
    /// </summary>
    /// <remarks>
    /// <b>Whatever is chosen is capped to the Tier 2 luminance ceiling before it is drawn</b>
    /// (<c>BarWindow.AccentColor</c>). A hover effect is the easiest place in a design to put
    /// something brighter than §2.3 allows, and a colour picker is an invitation to do exactly
    /// that; capping means the choice is about hue, which is taste, and not about how much light
    /// the panel emits, which is not.
    /// </remarks>
    public string HoverAccent { get; set; } = DefaultHoverAccent;

    /// <summary>The shipping hover accent: a cyan that reads as light passing over a surface.</summary>
    public const string DefaultHoverAccent = "#53E7FF";

    /// <summary>
    /// Which hover treatment the application icons use.
    /// </summary>
    /// <remarks>
    /// The row is the one place where the two defensible answers disagree. The plain chip is
    /// nearly free — <c>#1F1F1A</c> is L = 1.2%, about a thousandth of the wear of a white hover —
    /// while the holographic pane is a lit surface with a glow. Both are Tier 2 only, so neither
    /// is on the duty budget; the difference is how much light a hovered icon throws while the
    /// pointer rests on it, and that is the user's call rather than ours.
    /// </remarks>
    public AppIconHover AppIconHover { get; set; } = AppIconHover.Chip;

    /// <summary>Colour of the plain chip behind a hovered application icon.</summary>
    /// <remarks>
    /// Capped like the accent. The default is the §3.2 value the row was measured at.
    /// </remarks>
    public string AppIconHoverColour { get; set; } = DefaultAppIconHoverColour;

    /// <inheritdoc cref="AppIconHoverColour"/>
    public const string DefaultAppIconHoverColour = "#1F1F1A";

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

            case "applicationicons":
                if (bool.TryParse(value, out bool applicationIcons)) ApplicationIcons = applicationIcons;
                else Unusable(key, value);
                break;

            case "tray":
                if (bool.TryParse(value, out bool tray)) Tray = tray;
                else Unusable(key, value);
                break;

            case "monitor":
                Monitor = value;
                break;

            case "hoveraccent":
                if (IsColour(value)) HoverAccent = value;
                else Unusable(key, value);
                break;

            case "appiconhover":
                if (Enum.TryParse(value, ignoreCase: true, out AppIconHover appHover)) AppIconHover = appHover;
                else Unusable(key, value);
                break;

            case "appiconhovercolour":
            case "appiconhovercolor":
                if (IsColour(value)) AppIconHoverColour = value;
                else Unusable(key, value);
                break;

            default:
                BringUpLog.Write($"settings: ignoring unknown key '{key}'");
                break;
        }
    }

    /// <summary>
    /// Whether a value is a colour this can hand to WPF.
    /// </summary>
    /// <remarks>
    /// Checked here rather than left to the renderer, because a bad colour in
    /// <c>ColorConverter</c> throws, and a settings file is a text file someone may well edit by
    /// hand. The rule is the one the file's own comment states: a hex triplet, with or without
    /// alpha.
    /// </remarks>
    private static bool IsColour(string value)
    {
        if (value.Length is not (7 or 9) || value[0] != '#') return false;

        for (int i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i])) return false;
        }

        return true;
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
        "# Show each application's own icon rather than its current window's. Windows 11 shows",
        "# File Explorer the folder you are in; false restores that.",
        $"applicationIcons = {ApplicationIcons.ToString().ToLowerInvariant()}",
        "",
        "# Notification area hosting. On by default; costs a permanent 100 ms timer.",
        $"tray = {Tray.ToString().ToLowerInvariant()}",
        "",
        @"# Device name of the monitor to occupy, e.g. \\.\DISPLAY1. Empty means the primary.",
        $"monitor = {Monitor}",
        "",
        "# Colour of the holographic hover animation, as #RRGGBB or #AARRGGBB. Capped to the",
        "# Tier 2 luminance ceiling whatever you pick, so this chooses the hue and not the light.",
        $"hoverAccent = {HoverAccent}",
        "",
        "# chip | holographic -- what the application icons do on hover.",
        $"appIconHover = {AppIconHover}",
        "",
        "# Colour of the plain chip behind a hovered application icon.",
        $"appIconHoverColour = {AppIconHoverColour}",
    ];

    /// <summary>A copy, so a settings window can be cancelled without having mutated anything.</summary>
    public UmbraSettings Clone() => new()
    {
        Edge = Edge,
        Alignment = Alignment,
        DormantIconSize = DormantIconSize,
        Motion = Motion,
        ApplicationIcons = ApplicationIcons,
        Tray = Tray,
        Monitor = Monitor,
        HoverAccent = HoverAccent,
        AppIconHover = AppIconHover,
        AppIconHoverColour = AppIconHoverColour,
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
