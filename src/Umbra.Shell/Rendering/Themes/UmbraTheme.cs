using Umbra.Core.Color;

namespace Umbra.Shell.Rendering.Themes;

/// <summary>
/// The five colours a theme is made of. Everything else Umbra draws is derived from these.
/// </summary>
/// <param name="BarAccent">The hover accent on the bar: the holographic pane, Start while its menu is open, the attention dot. Capped to the Tier 2 ceiling when drawn.</param>
/// <param name="BarTint">The hue of the bar's resting greys, at their own luminance (<see cref="RestingTint"/>). A grey means none.</param>
/// <param name="WindowBackground">The background of Umbra's windows and panels.</param>
/// <param name="WindowText">Their ordinary text.</param>
/// <param name="WindowAccent">The selected page marker, focus rings and check marks.</param>
internal sealed record ThemeColours(
    string BarAccent, string BarTint, string WindowBackground, string WindowText, string WindowAccent)
{
    /// <summary>Same colours, compared as colours rather than as text (<c>#abc123</c> = <c>#ABC123</c>).</summary>
    public bool SameAs(ThemeColours other) =>
        Same(BarAccent, other.BarAccent) && Same(BarTint, other.BarTint)
        && Same(WindowBackground, other.WindowBackground) && Same(WindowText, other.WindowText)
        && Same(WindowAccent, other.WindowAccent);

    private static bool Same(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A named, curated set of <see cref="ThemeColours"/>.</summary>
internal sealed record ThemePreset(string Name, ThemeColours Colours);

/// <summary>
/// The curated themes, and how a window palette is derived from a theme's five colours.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every theme is dark, and the bar stays black in all of them.</b> A lit background would
/// light the whole strip, which is the wear Umbra exists to remove; a light settings window beside
/// a black bar would be the brightest thing on the panel. A theme colours what is drawn on the
/// black — the hover accent and the hue of the resting greys — and both stay inside the budget:
/// the accent through the Tier 2 cap, the tint by holding luminance (<see cref="RestingTint"/>).
/// </para>
/// <para>
/// <b>There is no stored theme name.</b> The colours are the truth and the preset list follows
/// them, as the hover colour's list always has: a theme is selected when its colours match, and
/// editing any one of them makes it Custom. So an existing file whose hover accent was set by hand
/// opens as Custom with that accent intact, rather than being overwritten by a default theme.
/// </para>
/// </remarks>
internal static class UmbraTheme
{
    /// <summary>Umbra's own colours, and the default: exactly what the bar and windows drew before themes.</summary>
    public static readonly ThemePreset Default = new("Umbra",
        new("#53E7FF", "#808080", "#14140F", "#B6B6AD", "#B0F7FB"));

    /// <summary>The curated list, the default first.</summary>
    public static readonly IReadOnlyList<ThemePreset> Presets =
    [
        Default,
        new("Graphite", new("#9DB8FF", "#8A93A6", "#121316", "#B4B8C2", "#9DB8FF")),
        new("Glacier",  new("#7FD4E6", "#8C9EAA", "#11161A", "#B3C1CA", "#8FD3E3")),
        new("Moss",     new("#8EE08E", "#8FA68F", "#11140F", "#B3BDAE", "#A8E0A3")),
        new("Ember",    new("#FFA060", "#A69080", "#16120F", "#C4B6A8", "#FFB580")),
        new("Rose",     new("#FF8FB3", "#AD8F99", "#171214", "#C6B4BA", "#FFA8C4")),
        new("Dusk",     new("#B9A0FF", "#9A8FAD", "#141218", "#BCB4C6", "#C6B4FF")),
    ];

    /// <summary>The preset these colours are, or null for Custom.</summary>
    public static ThemePreset? Matching(ThemeColours colours) =>
        Presets.FirstOrDefault(p => p.Colours.SameAs(colours));

    /// <summary>The colour behind <paramref name="hex"/>, or <paramref name="fallback"/> if it is not one.</summary>
    public static Rgb Parse(string hex, Rgb fallback)
    {
        string trimmed = hex.Trim();

        // #AARRGGBB is accepted by the settings file for the hover colours; the alpha is ignored.
        if (trimmed.Length == 9 && trimmed[0] == '#') trimmed = "#" + trimmed[3..];

        try { return Rgb.FromHex(trimmed); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return fallback; }
    }

    /// <summary>The tint for the bar's greys.</summary>
    public static RestingTint TintOf(ThemeColours colours) =>
        RestingTint.Toward(Parse(colours.BarTint, Rgb.FromHex(Default.Colours.BarTint)));

    /// <summary>Every colour Umbra's windows and panels draw, derived from a theme.</summary>
    public static WindowPalette Palette(ThemeColours colours)
    {
        Rgb bg = Parse(colours.WindowBackground, Rgb.FromHex(Default.Colours.WindowBackground));
        Rgb text = Parse(colours.WindowText, Rgb.FromHex(Default.Colours.WindowText));
        Rgb accent = Parse(colours.WindowAccent, Rgb.FromHex(Default.Colours.WindowAccent));

        // The steps between background and text are the ones the hand-picked default used,
        // measured off it as fractions of the way from one to the other. Window chrome is not on
        // the wear budget, so these are mixed in sRGB values, which is what they were picked in.
        // Brighter text is lifted toward the text's own hue at full brightness rather than toward
        // white, so a warm theme's headings stay warm.
        int peak = Math.Max(1, (int)Math.Max(text.R, Math.Max(text.G, text.B)));
        Rgb brightest = new(
            (byte)Math.Round(text.R * 255.0 / peak),
            (byte)Math.Round(text.G * 255.0 / peak),
            (byte)Math.Round(text.B * 255.0 / peak));

        Rgb Step(double t) => Mix(bg, text, t);
        Rgb Lift(double t) => Mix(text, brightest, t);

        return new WindowPalette(
            Background: bg,
            Surface: Step(0.045),
            NavHover: Step(0.062),
            Control: Step(0.093),
            NavSelected: Step(0.111),
            ControlHover: Step(0.148),
            Divider: Step(0.160),
            Highlight: Step(0.191),
            Border: Step(0.235),
            Thumb: Step(0.309),
            BorderHover: Step(0.333),
            TextDim: Step(0.555),
            TextHint: Step(0.728),
            Glyph: Step(0.827),
            Text: text,
            TextItem: Lift(0.575),
            TextStrong: Lift(0.685),
            Accent: accent,
            Warning: Rgb.FromHex("#C8B48A"));
    }

    private static Rgb Mix(Rgb a, Rgb b, double t) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}

/// <summary>The colours of Umbra's windows and panels, darkest to lightest, then the accents.</summary>
internal sealed record WindowPalette(
    Rgb Background, Rgb Surface, Rgb NavHover, Rgb Control, Rgb NavSelected, Rgb ControlHover,
    Rgb Divider, Rgb Highlight, Rgb Border, Rgb Thumb, Rgb BorderHover,
    Rgb TextDim, Rgb TextHint, Rgb Glyph, Rgb Text, Rgb TextItem, Rgb TextStrong,
    Rgb Accent, Rgb Warning);
