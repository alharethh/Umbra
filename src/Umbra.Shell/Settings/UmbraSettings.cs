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

/// <summary>Whether the clock counts the day in 24 hours or 12.</summary>
internal enum ClockFormat
{
    /// <summary>Whatever Windows' own short time format uses, as the Windows clock does.</summary>
    Windows,

    /// <summary>Always 24-hour, which was the only clock before this setting existed.</summary>
    TwentyFourHour,

    /// <summary>Always 12-hour, with AM and PM.</summary>
    TwelveHour,
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
    /// <b>Defaults to 16, not to §7.1's 24.</b> This is a deliberate deviation, a product
    /// choice: the bar ships with idle icons shrunk and growing back on approach, rather
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

    /// <summary>
    /// How much of their designed light the resting tiers keep, in percent: 100 is the §3.1 table,
    /// 10 is the floor.
    /// </summary>
    /// <remarks>
    /// Only ever darker, and only at rest; pointing at the bar is as bright as it always was. See
    /// <see cref="Umbra.Core.Tiers.TierPalette.Resolve"/>. A percentage rather than a fraction so
    /// the file reads the way the slider does.
    /// </remarks>
    public int RestingBrightness { get; set; } = 100;

    /// <summary>The lowest <see cref="RestingBrightness"/> the file accepts.</summary>
    public static int MinRestingBrightness =>
        (int)Math.Round(Umbra.Core.Tiers.TierPalette.MinRestingBrightness * 100);

    /// <summary>Whether the motion layer runs (§6).</summary>
    public bool Motion { get; set; } = true;

    /// <summary>
    /// Minutes without input, with nothing playing, before the bar fades to black. Zero is off.
    /// </summary>
    /// <remarks>
    /// On at ten minutes by default, the project owner's choice. Read as a number rather than from
    /// a fixed list so a hand-edited file can shorten it for a live check; the settings window
    /// offers Off, 15 and 30 seconds, and 1, 5, 10 and 30 minutes.
    /// </remarks>
    public double AwayAfterMinutes { get; set; } = 10.0;

    /// <summary>The longest delay the file accepts: a day.</summary>
    public const double MaxAwayAfterMinutes = 24 * 60;

    /// <summary>
    /// Whether Umbra starts when the user signs in.
    /// </summary>
    /// <remarks>
    /// <b>Off by default, and that is deliberate rather than timid.</b> An application that
    /// registers itself to start at sign-in without being asked is one people uninstall. The
    /// checkbox is the ask.
    /// </remarks>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Whether a bar appears on every monitor rather than only on <see cref="Monitor"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, and that is a wear decision rather than caution.</b> §11.2 Q12 asks
    /// whether the OLED treatment should apply per monitor at all: on a non-OLED secondary every
    /// bit of it is cost without benefit — the dim tiers, the monochrome row, the shrunken icons
    /// are all paid for in legibility and bought nothing, because an LCD does not wear
    /// differentially. Turning this on gives that screen the same treatment as the OLED one,
    /// which may be the wrong trade for the user's hardware. Until per-monitor treatment exists,
    /// leaving it off is the honest default.
    /// </para>
    /// <para>
    /// The notification area stays on the primary screen alone whatever this says. Windows does
    /// the same, and it is forced here anyway: ManagedShell's tray is one collection fed by one
    /// hidden window, so a second row bound to it would draw every icon twice.
    /// </para>
    /// </remarks>
    public bool BarOnEveryMonitor { get; set; }

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
    /// <b>On by default</b> — a deliberate choice, 2026-09-20. It was off because tray
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

    /// <summary>12 or 24 hours on the clock.</summary>
    /// <remarks>
    /// Follows Windows by default, as the Windows clock does. Before this setting the clock was
    /// always 24-hour, so a user whose Windows is set to 12-hour sees it change on updating; that
    /// was the owner's call, because the old clock ignored their setting.
    /// </remarks>
    public ClockFormat ClockFormat { get; set; } = ClockFormat.Windows;

    /// <summary>
    /// Device name of the monitor to occupy, or empty for the primary.
    /// </summary>
    /// <remarks>
    /// Stored as the device name (<c>\\.\DISPLAY1</c>) rather than an index, because indices are
    /// reassigned when a monitor is unplugged and the bar would silently move to a different
    /// screen. A name that no longer matches any attached screen falls back to the primary.
    /// </remarks>
    public string Monitor { get; set; } = string.Empty;

    /// <summary>
    /// Whether the one-time "right-click the bar for settings" note has been shown.
    /// </summary>
    /// <remarks>
    /// Set as soon as the note appears, not when it is dismissed, so a note that times out unread
    /// is still never shown again. Existing installs start false and see it once too: the users
    /// who most needed it were the ones already running Umbra without knowing about the menu.
    /// </remarks>
    public bool SettingsHintShown { get; set; }

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
                if (TryParseName(value, out BarEdge edge)) Edge = edge;
                else Unusable(key, value);
                break;

            case "alignment":
                if (TryParseName(value, out Alignment alignment)) Alignment = alignment;
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

            case "restingbrightness":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int brightness)
                    && brightness >= MinRestingBrightness && brightness <= 100)
                {
                    RestingBrightness = brightness;
                }
                else Unusable(key, value);
                break;

            case "awayafterminutes":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double away)
                    && away >= 0 && away <= MaxAwayAfterMinutes)
                {
                    AwayAfterMinutes = away;
                }
                else Unusable(key, value);
                break;

            case "motion":
                if (bool.TryParse(value, out bool motion)) Motion = motion;
                else Unusable(key, value);
                break;

            case "startwithwindows":
                if (bool.TryParse(value, out bool startWithWindows)) StartWithWindows = startWithWindows;
                else Unusable(key, value);
                break;

            case "baroneverymonitor":
                if (bool.TryParse(value, out bool everyMonitor)) BarOnEveryMonitor = everyMonitor;
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

            case "settingshintshown":
                if (bool.TryParse(value, out bool hintShown)) SettingsHintShown = hintShown;
                else Unusable(key, value);
                break;

            case "hoveraccent":
                if (IsColour(value)) HoverAccent = value;
                else Unusable(key, value);
                break;

            case "appiconhover":
                if (TryParseName(value, out AppIconHover appHover)) AppIconHover = appHover;
                else Unusable(key, value);
                break;

            case "appiconhovercolour":
            case "appiconhovercolor":
                if (IsColour(value)) AppIconHoverColour = value;
                else Unusable(key, value);
                break;

            case "clockformat":
                if (TryParseName(value, out ClockFormat clockFormat)) ClockFormat = clockFormat;
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

    /// <summary>
    /// Reads an enum by one of its declared names, case-insensitively, and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>Enum.TryParse</c> is the obvious call and the wrong one: it also accepts any integer,
    /// declared or not, and comma-joined names, which it ORs together. <c>edge = 7</c> came back as
    /// a successful parse of <c>(BarEdge)7</c>, and <c>BarMetrics</c> threw on it at startup, after
    /// <c>ShellHost</c> had already suppressed Explorer's taskbar, on every launch until the file
    /// was edited by hand. This file only ever writes names, so a number is never something Umbra
    /// wrote, and accepting names alone rules out the integer and the combination together.
    /// </remarks>
    internal static bool TryParseName<TEnum>(string value, out TEnum result) where TEnum : struct, Enum
    {
        string candidate = value.Trim();

        foreach (string name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
            {
                result = Enum.Parse<TEnum>(name);
                return true;
            }
        }

        result = default;
        return false;
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
        "# Percent of the resting greys' light, 10 to 100. Never brighter than the default;",
        "# pointing at the bar is unaffected. Half the light is about a third of the wear.",
        string.Create(CultureInfo.InvariantCulture, $"restingBrightness = {RestingBrightness}"),
        "",
        "# The drift and micro-jitter layer.",
        $"motion = {Motion.ToString().ToLowerInvariant()}",
        "",
        "# Minutes without input before the bar fades to black, unless something is playing",
        "# sound. 0 turns it off. Any input brings the bar back at once.",
        string.Create(CultureInfo.InvariantCulture, $"awayAfterMinutes = {AwayAfterMinutes}"),
        "",
        "# Start Umbra when you sign in. Written to HKCU Run, and visible in Task Manager's",
        "# Startup tab, so it can be turned off from there too.",
        $"startWithWindows = {StartWithWindows.ToString().ToLowerInvariant()}",
        "",
        "# A bar on every monitor rather than only the one named below. Off by default: a",
        "# non-OLED second screen pays the dim tiers and monochrome icons and gets nothing back.",
        $"barOnEveryMonitor = {BarOnEveryMonitor.ToString().ToLowerInvariant()}",
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
        "# Whether the one-time note about right-clicking the bar for settings has been shown.",
        $"settingsHintShown = {SettingsHintShown.ToString().ToLowerInvariant()}",
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
        "",
        "# windows | twentyFourHour | twelveHour -- the clock's hours. windows follows the",
        "# short time format in Windows' own region settings.",
        $"clockFormat = {ClockFormat}",
    ];

    /// <summary>A copy, so a settings window can be cancelled without having mutated anything.</summary>
    public UmbraSettings Clone() => new()
    {
        Edge = Edge,
        Alignment = Alignment,
        DormantIconSize = DormantIconSize,
        RestingBrightness = RestingBrightness,
        Motion = Motion,
        AwayAfterMinutes = AwayAfterMinutes,
        StartWithWindows = StartWithWindows,
        BarOnEveryMonitor = BarOnEveryMonitor,
        ApplicationIcons = ApplicationIcons,
        Tray = Tray,
        Monitor = Monitor,
        SettingsHintShown = SettingsHintShown,
        HoverAccent = HoverAccent,
        AppIconHover = AppIconHover,
        AppIconHoverColour = AppIconHoverColour,
        ClockFormat = ClockFormat,
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
    public bool NeedsRestartToReach(UmbraSettings other) =>
        Tray != other.Tray || BarOnEveryMonitor != other.BarOnEveryMonitor;
}
