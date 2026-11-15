using Umbra.Core.Color;
using Umbra.Core.Tiers;
using Umbra.Shell.Rendering.Themes;

namespace Umbra.Shell.Settings;

/// <summary>What a colour in the settings window is for, which decides what is really drawn.</summary>
internal enum ColourRole
{
    /// <summary>The bar's accent: hover, Start, the clock and tray at Tier 2. Capped.</summary>
    BarAccent,

    /// <summary>The application icons' hover chip. Capped like the accent.</summary>
    HoverChip,

    /// <summary>The resting icons' tint. Only its hue counts; brightness stays the bar's.</summary>
    RestingTint,

    /// <summary>Umbra's own windows and panels, drawn exactly as picked.</summary>
    WindowBackground,
    WindowText,
    WindowAccent,
}

/// <summary>
/// The colour picker's rules, free of WPF: what a pick will really look like on screen, the
/// presets offered for each colour, and how a pick is written back into its hex box.
/// </summary>
/// <remarks>
/// <b>The picker shows what will be drawn, not only what was picked.</b> The bar caps its accent
/// and hover chip to the Tier 2 luminance ceiling and applies the tint as a luminance-1
/// multiplier, so a bright white accent is drawn as a mid grey and a dark blue tint as a full
/// resting blue. A picker that showed the raw colour alone would promise something the bar then
/// silently does not do. These are the same calls the bar and the theme tiles make.
/// </remarks>
internal static class ColourPick
{
    /// <summary>What the bar or window draws for <paramref name="picked"/>.</summary>
    public static Rgb Drawn(ColourRole role, Rgb picked) => role switch
    {
        ColourRole.BarAccent or ColourRole.HoverChip => Srgb.CapLuminance(picked, TierPalette.Tier2LuminanceCap),

        // The Tier 2 grey, as the theme tiles show it: the resting grey itself is nearly black,
        // which would show no hue at all on a swatch.
        ColourRole.RestingTint => RestingTint.Toward(picked).Apply(TintPreviewGrey),

        _ => picked,
    };

    /// <summary>The grey a tint is shown on.</summary>
    public static readonly Rgb TintPreviewGrey = Rgb.FromHex("#808080");

    /// <summary>What the "drawn" swatch is called for this colour, or null when it is drawn as picked.</summary>
    public static string? DrawnLabel(ColourRole role) => role switch
    {
        ColourRole.BarAccent => "On the bar",
        ColourRole.HoverChip => "On the bar",
        ColourRole.RestingTint => "Resting icons",
        _ => null,
    };

    /// <summary>One line under the preview saying why the two differ, or null when they cannot.</summary>
    public static string? DrawnNote(ColourRole role) => role switch
    {
        ColourRole.BarAccent or ColourRole.HoverChip => "Capped to the brightest a hover may be, so it adds no wear.",
        ColourRole.RestingTint => "Only the hue counts: the icons keep their own brightness.",
        _ => null,
    };

    /// <summary>
    /// The hex box's new text for a pick. Written as <c>#RRGGBB</c>; a box that held
    /// <c>#AARRGGBB</c> (the settings file accepts it for the hover colours) keeps its alpha.
    /// </summary>
    public static string Hex(Rgb picked, string previous)
    {
        string trimmed = previous.Trim();
        return trimmed.Length == 9 && trimmed[0] == '#' && IsHex(trimmed.AsSpan(1, 2))
            ? "#" + trimmed.Substring(1, 2).ToUpperInvariant() + picked.ToHex()[1..]
            : picked.ToHex();
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// A row of presets for this colour: the themes' own colours for it first, then a few more.
    /// Hues for the accents and tint, darks for the background and the hover chip, lights for the text.
    /// </summary>
    public static IReadOnlyList<Rgb> Presets(ColourRole role)
    {
        // The hover chip is not a theme colour; its default is a dark chip, so its presets are too.
        IEnumerable<string> themes = role == ColourRole.HoverChip ? [] : UmbraTheme.Presets.Select(p => role switch
        {
            ColourRole.BarAccent => p.Colours.BarAccent,
            ColourRole.RestingTint => p.Colours.BarTint,
            ColourRole.WindowBackground => p.Colours.WindowBackground,
            ColourRole.WindowText => p.Colours.WindowText,
            _ => p.Colours.WindowAccent,
        });

        string[] extra = role switch
        {
            ColourRole.WindowBackground => ["#000000", "#0B0B0D", "#101418", "#1A1A1F", "#1B1612"],
            ColourRole.WindowText => ["#FFFFFF", "#D8D8D8", "#9A9A9A", "#E6DCC8", "#C8DCE6"],
            ColourRole.HoverChip => [UmbraSettings.DefaultAppIconHoverColour, "#262626", "#3A3A3A", "#1A2430", "#1F2A1F", "#2A1F1A", "#2A1F26", "#221F2E", "#53E7FF", "#FFFFFF"],
            _ => ["#FFFFFF", "#FF5C5C", "#FFD24D", "#4DA6FF", "#5CE0B8"],
        };

        // Distinct and in order: two themes can share a colour (Graphite's accents are one).
        return [.. themes.Concat(extra).Select(Rgb.FromHex).Distinct().Take(12)];
    }
}
