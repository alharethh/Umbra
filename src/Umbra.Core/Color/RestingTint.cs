namespace Umbra.Core.Color;

/// <summary>
/// A hue for the bar's monochrome greys that leaves their luminance exactly where it was.
/// </summary>
/// <remarks>
/// <para>
/// A theme may colour the resting icons, the clock and Start, but the §3.1 greys are what §2.3's
/// duty budget is built on, so a tint is a per-channel multiplier in <b>linear light</b> whose own
/// relative luminance is 1: <c>Kr·R + Kg·G + Kb·B = 1</c>. Applied to a grey, which has equal
/// linear channels, it changes the hue and leaves <c>L</c> — and therefore <c>L^n</c>, the wear —
/// unchanged. Because it is a fixed linear map, a ramp between two tinted greys is the tint of
/// the ramp, so §3.4's linear-light interpolation is unaffected.
/// </para>
/// <para>
/// <b>No channel may be driven more than <see cref="MaxChannelGain"/> times the grey.</b> Equal
/// luminance from a saturated blue would mean one subpixel at many times the grey's drive (blue
/// carries 7% of luminance), and luminance is not the whole story for a single subpixel's ageing.
/// A colour that would exceed the gain is pulled toward neutral until it does not, so a vivid
/// pick becomes a gentle tint of the same hue rather than a blue strip.
/// </para>
/// </remarks>
public readonly record struct RestingTint(double R, double G, double B)
{
    /// <summary>The most any channel may be multiplied by.</summary>
    public const double MaxChannelGain = 2.0;

    /// <summary>No tint: the §3.1 greys exactly.</summary>
    public static RestingTint None => new(1.0, 1.0, 1.0);

    /// <summary>True for <see cref="None"/>, which callers use to skip the work entirely.</summary>
    public bool IsNone => R == 1.0 && G == 1.0 && B == 1.0;

    /// <summary>
    /// The tint toward <paramref name="colour"/>'s hue. A grey, or black, is <see cref="None"/>.
    /// </summary>
    /// <remarks>
    /// The colour's own brightness does not matter, only its chromaticity: <c>#404060</c> and
    /// <c>#8080C0</c> give the same tint. Its saturation does, up to the gain limit.
    /// </remarks>
    public static RestingTint Toward(Rgb colour)
    {
        (double r, double g, double b) = Srgb.ToLinear(colour);
        double luminance = Srgb.RelativeLuminance(r, g, b);
        if (luminance <= 1e-6) return None;

        // The colour scaled to luminance 1: the full-strength multiplier.
        double fr = r / luminance, fg = g / luminance, fb = b / luminance;

        // Pull toward neutral (1, 1, 1) until no channel exceeds the gain. Interpolating two
        // multipliers of luminance 1 keeps luminance 1, so this never changes the light.
        double peak = Math.Max(fr, Math.Max(fg, fb));
        double strength = peak > MaxChannelGain ? (MaxChannelGain - 1.0) / (peak - 1.0) : 1.0;

        RestingTint tint = new(
            1.0 + (fr - 1.0) * strength,
            1.0 + (fg - 1.0) * strength,
            1.0 + (fb - 1.0) * strength);

        // A grey's own rounding leaves it a hair off neutral; call that neutral, so the default
        // theme draws byte-for-byte what the bar always drew.
        return Math.Abs(tint.R - 1.0) < 1e-3 && Math.Abs(tint.G - 1.0) < 1e-3 && Math.Abs(tint.B - 1.0) < 1e-3
            ? None
            : tint;
    }

    /// <summary>Applies the tint to one colour, in linear light.</summary>
    public Rgb Apply(Rgb colour)
    {
        if (IsNone) return colour;

        (double r, double g, double b) = Srgb.ToLinear(colour);
        return Srgb.FromLinear(r * R, g * G, b * B);
    }
}
