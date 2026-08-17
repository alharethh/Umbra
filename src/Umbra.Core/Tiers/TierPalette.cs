using Umbra.Core.Color;

namespace Umbra.Core.Tiers;

/// <summary>How an element is painted in a given tier.</summary>
public enum TierAppearanceKind
{
    /// <summary>Not drawn at all. The cheapest state there is (P1).</summary>
    Hidden,

    /// <summary>A flat monochrome colour.</summary>
    Solid,

    /// <summary>The source artwork's own colour, tone-mapped to <see cref="TierPalette.Tier2LuminanceCap"/>.</summary>
    FullChromaCapped,

    /// <summary>The system accent colour, held steady.</summary>
    SystemAccent,
}

/// <summary>One cell of the §3.1 table.</summary>
public readonly record struct TierAppearance(TierAppearanceKind Kind, Rgb Colour)
{
    /// <summary>Not drawn.</summary>
    public static TierAppearance Hidden => new(TierAppearanceKind.Hidden, Rgb.Black);

    /// <summary>A flat colour.</summary>
    public static TierAppearance Solid(string hex) => new(TierAppearanceKind.Solid, Rgb.FromHex(hex));

    /// <summary>Full chroma, luminance-capped.</summary>
    public static TierAppearance FullChroma => new(TierAppearanceKind.FullChromaCapped, Rgb.Black);

    /// <summary>System accent, steady.</summary>
    public static TierAppearance Accent => new(TierAppearanceKind.SystemAccent, Rgb.Black);

    /// <summary>
    /// The colour actually emitted, treating <see cref="TierAppearanceKind.Hidden"/> as black —
    /// which against a black background it is.
    /// </summary>
    public Rgb EffectiveColour => Kind == TierAppearanceKind.Hidden ? Rgb.Black : Colour;
}

/// <summary>A resolved appearance at a continuous tier level, ready to paint.</summary>
/// <param name="Monochrome">The flat colour to paint, or the base the chroma blend starts from.</param>
/// <param name="ChromaBlend">0 for pure monochrome, 1 for fully capped source chroma.</param>
/// <param name="UsesSystemAccent">True when <paramref name="ChromaBlend"/> runs toward the system accent rather than the artwork.</param>
public readonly record struct TierRender(Rgb Monochrome, double ChromaBlend, bool UsesSystemAccent)
{
    /// <summary>True when the element emits nothing at all.</summary>
    public bool IsDark => ChromaBlend <= 0.0 && Monochrome == Rgb.Black;
}

/// <summary>
/// The tier values of design-spec §3.1, and the §3.4 rule for interpolating between them.
/// </summary>
/// <remarks>
/// Tier 1 sits one step darker than v1 throughout, and Tier 2's greys drop from <c>#808080</c> to
/// <c>#666666</c>: against pure black, 26.6 nits is emphatically legible (§3.1).
/// </remarks>
public static class TierPalette
{
    /// <summary>
    /// The Tier 2 luminance cap, <c>L ≤ 0.45</c> — roughly 90 nits at a 200-nit reference (§3.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Icons are tone-mapped so their peak luminance lands here; hue and saturation are untouched.
    /// Against a pure black bar a 90-nit icon reads as unambiguously "full colour" — the
    /// perceptual cue for colour is chroma, not luminance, and there is no adjacent bright content
    /// to compare against.
    /// </para>
    /// <para>
    /// This is the highest-value change in v2 and it costs almost nothing: a 3.9× reduction in
    /// Tier 2 cost, and it removes the design's dependence on an unmeasured hover duty. "Full
    /// colour" was always the intent; "full luminance" never was.
    /// </para>
    /// </remarks>
    public const double Tier2LuminanceCap = 0.45;

    /// <summary>Ambient-band radius in pixels at 100% scaling — Tier 1 begins here (§3).</summary>
    public const double AmbientBandPixels = 120.0;

    /// <summary>Engaged-band radius in pixels at 100% scaling — Tier 2 begins here (§3).</summary>
    public const double EngagedBandPixels = 40.0;

    /// <summary>The §3.1 table.</summary>
    public static TierAppearance Appearance(BarElement element, Tier tier)
    {
        // "—" in the §3.1 table means the element has no distinct alert appearance; it keeps
        // whatever Tier 2 gives it.
        if (tier == Tier.Alert && element != BarElement.NotificationBadge) tier = Tier.Engaged;

        return element switch
        {
            BarElement.Background => TierAppearance.Solid("#000000"),

            BarElement.PinnedIcon => tier switch
            {
                Tier.Dormant => TierAppearance.Solid("#333333"),
                Tier.Ambient => TierAppearance.Solid("#404040"),
                _ => TierAppearance.FullChroma,
            },

            BarElement.RunningIcon => tier switch
            {
                Tier.Dormant => TierAppearance.Solid("#404040"),
                Tier.Ambient => TierAppearance.Solid("#4D4D4D"),
                _ => TierAppearance.FullChroma,
            },

            BarElement.RunningIndicator => tier switch
            {
                Tier.Dormant => TierAppearance.Hidden,
                Tier.Ambient => TierAppearance.Solid("#404040"),
                _ => TierAppearance.Solid("#666666"),
            },

            BarElement.Clock or BarElement.StartGlyph => tier switch
            {
                Tier.Dormant => TierAppearance.Solid("#333333"),
                Tier.Ambient => TierAppearance.Solid("#404040"),
                _ => TierAppearance.Solid("#666666"),
            },

            BarElement.TrayIcon => tier switch
            {
                Tier.Dormant => TierAppearance.Solid("#333333"),
                Tier.Ambient => TierAppearance.Solid("#404040"),
                _ => TierAppearance.FullChroma,
            },

            BarElement.NotificationBadge => tier switch
            {
                Tier.Dormant or Tier.Ambient => TierAppearance.Solid("#B3B3B3"),
                _ => TierAppearance.Accent,
            },

            _ => throw new ArgumentOutOfRangeException(nameof(element)),
        };
    }

    /// <summary>
    /// Resolves an element at a continuous tier level in 0..2, interpolating <b>in linear light</b>
    /// per §3.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interpolating in sRGB values instead makes the ramp spend more time bright than intended —
    /// a wear error, not only a cosmetic one.
    /// </para>
    /// <para>
    /// Where Tier 2 is full chroma there is no colour to interpolate toward, so the monochrome
    /// value holds at its Tier 1 setting and <see cref="TierRender.ChromaBlend"/> cross-fades to
    /// the capped artwork instead.
    /// </para>
    /// </remarks>
    public static TierRender Resolve(BarElement element, double level)
    {
        level = Math.Clamp(level, 0.0, 2.0);

        int lower = (int)Math.Floor(level);
        if (lower >= 2) lower = 1;
        int upper = lower + 1;
        double fraction = level - lower;

        TierAppearance from = Appearance(element, (Tier)lower);
        TierAppearance to = Appearance(element, (Tier)upper);

        bool blendsToChroma = to.Kind is TierAppearanceKind.FullChromaCapped or TierAppearanceKind.SystemAccent;
        if (blendsToChroma)
            return new TierRender(from.EffectiveColour, fraction, to.Kind == TierAppearanceKind.SystemAccent);

        return new TierRender(
            Srgb.Lerp(from.EffectiveColour, to.EffectiveColour, fraction),
            ChromaBlend: 0.0,
            UsesSystemAccent: false);
    }
}
