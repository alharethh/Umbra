namespace Umbra.Core.Color;

/// <summary>
/// sRGB transfer functions and the operations the spec performs in linear light.
/// </summary>
/// <remarks>
/// <para>
/// Every luminance in design-spec §2.1 is relative luminance as a fraction of panel peak,
/// computed with the piecewise-sRGB decode and Rec. 709 coefficients (§5.1 step 2).
/// </para>
/// <para>
/// <b>Scaling, capping and interpolation all happen in linear light.</b> Doing any of them on
/// encoded sRGB values is wrong, and per §3.4 a ramp interpolated in sRGB "will look wrong and
/// spend more time bright than intended" — which is a wear error, not only a cosmetic one.
/// </para>
/// </remarks>
public static class Srgb
{
    /// <summary>Encoded value at or below which the transfer function is linear.</summary>
    public const double LinearSegmentEnd = 0.04045;

    /// <summary>Linear value at or below which the inverse transfer function is linear.</summary>
    public const double LinearSegmentEndInverse = 0.0031308;

    /// <summary>Slope of the linear segment.</summary>
    public const double LinearSlope = 12.92;

    /// <summary>Exponent of the power segment.</summary>
    public const double Gamma = 2.4;

    /// <summary>Offset of the power segment.</summary>
    public const double Offset = 0.055;

    /// <summary>Rec. 709 red coefficient (§5.1 step 2).</summary>
    public const double Kr = 0.2126;

    /// <summary>Rec. 709 green coefficient.</summary>
    public const double Kg = 0.7152;

    /// <summary>Rec. 709 blue coefficient.</summary>
    public const double Kb = 0.0722;

    /// <summary>Decodes one sRGB channel (0..1) to linear light (0..1).</summary>
    public static double ToLinear(double encoded) =>
        encoded <= LinearSegmentEnd
            ? encoded / LinearSlope
            : Math.Pow((encoded + Offset) / (1.0 + Offset), Gamma);

    /// <summary>Encodes one linear-light channel (0..1) back to sRGB (0..1).</summary>
    public static double ToEncoded(double linear) =>
        linear <= LinearSegmentEndInverse
            ? linear * LinearSlope
            : (1.0 + Offset) * Math.Pow(linear, 1.0 / Gamma) - Offset;

    /// <summary>Decodes an 8-bit channel to linear light.</summary>
    public static double ToLinear(byte channel) => ToLinear(channel / 255.0);

    /// <summary>Encodes a linear-light channel to 8 bits, clamped and rounded.</summary>
    public static byte ToByte(double linear) =>
        (byte)Math.Clamp(Math.Round(ToEncoded(Math.Clamp(linear, 0.0, 1.0)) * 255.0), 0.0, 255.0);

    /// <summary>Relative luminance of an 8-bit colour, as a fraction of panel peak.</summary>
    public static double RelativeLuminance(Rgb colour) =>
        RelativeLuminance(ToLinear(colour.R), ToLinear(colour.G), ToLinear(colour.B));

    /// <summary>Relative luminance from already-linear channels.</summary>
    public static double RelativeLuminance(double linearR, double linearG, double linearB) =>
        Kr * linearR + Kg * linearG + Kb * linearB;

    /// <summary>Linear-light RGB of an 8-bit colour.</summary>
    public static (double R, double G, double B) ToLinear(Rgb colour) =>
        (ToLinear(colour.R), ToLinear(colour.G), ToLinear(colour.B));

    /// <summary>Rebuilds an 8-bit colour from linear-light channels, clamping to gamut.</summary>
    public static Rgb FromLinear(double r, double g, double b) =>
        new(ToByte(r), ToByte(g), ToByte(b));

    /// <summary>
    /// Multiplies a colour's linear light by <paramref name="factor"/>, preserving chromaticity.
    /// Channels that clip are clamped, so the result's luminance can fall short of
    /// <c>factor × L</c> when the factor pushes a channel out of gamut.
    /// </summary>
    public static Rgb Scale(Rgb colour, double factor)
    {
        (double r, double g, double b) = ToLinear(colour);
        return FromLinear(r * factor, g * factor, b * factor);
    }

    /// <summary>
    /// The design-spec §3.2 Tier 2 treatment: tone-map so peak luminance lands on
    /// <paramref name="cap"/>, leaving hue and saturation untouched. Colours already at or below
    /// the cap are returned unchanged.
    /// </summary>
    /// <remarks>
    /// The result's luminance matches the cap only to 8-bit quantisation — expect a few
    /// thousandths of error, and in either direction.
    /// </remarks>
    public static Rgb CapLuminance(Rgb colour, double cap)
    {
        double luminance = RelativeLuminance(colour);
        if (luminance <= cap || luminance <= 0.0) return colour;
        return Scale(colour, cap / luminance);
    }

    /// <summary>
    /// Interpolates between two colours <b>in linear light</b> — the §3.4 proximity ramp.
    /// <paramref name="t"/> is clamped to 0..1.
    /// </summary>
    public static Rgb Lerp(Rgb from, Rgb to, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        (double r0, double g0, double b0) = ToLinear(from);
        (double r1, double g1, double b1) = ToLinear(to);
        return FromLinear(
            r0 + (r1 - r0) * t,
            g0 + (g1 - g0) * t,
            b0 + (b1 - b0) * t);
    }
}
