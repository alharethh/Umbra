using Umbra.Core.Color;

namespace Umbra.Core.Wear;

/// <summary>
/// The wear model of design-spec §2 (P2): degradation follows <c>lifetime ∝ 1/J^n</c>, so
/// accumulated wear per unit time goes as <c>L^n</c> with <c>n ≈ 1.5–2.0</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wear is superlinear.</b> v1 of the spec accumulated <c>L · Δt</c>, which measures emitted
/// light rather than degradation and systematically under-ranks bright elements — backwards for a
/// tool whose job is to find them (§9). Everything here raises to <see cref="DefaultExponent"/>
/// first; there is deliberately no linear path through this API.
/// </para>
/// <para>
/// The exponent is a planning assumption, not a measurement, so it travels with the data
/// (<see cref="WearAccumulator.Exponent"/>) and a collected dataset can be re-ranked for a
/// different <c>n</c> without re-collecting.
/// </para>
/// </remarks>
public static class WearModel
{
    /// <summary>The planning exponent, <c>n = 1.7</c> (§2, P2).</summary>
    public const double DefaultExponent = 1.7;

    /// <summary>Wear per second of a surface held at <paramref name="luminance"/>, normalised to white.</summary>
    public static double Rate(double luminance, double exponent = DefaultExponent)
    {
        if (luminance <= 0.0) return 0.0;   // P1: a black pixel draws no current, and 0^n is 0.
        return Math.Pow(luminance, exponent);
    }

    /// <summary>Wear per second of a colour held at rest.</summary>
    public static double Rate(Rgb colour, double exponent = DefaultExponent) =>
        Rate(Srgb.RelativeLuminance(colour), exponent);

    /// <summary>Wear accumulated by holding <paramref name="luminance"/> for <paramref name="seconds"/>.</summary>
    public static double Accumulate(double luminance, double seconds, double exponent = DefaultExponent) =>
        Rate(luminance, exponent) * seconds;

    /// <summary>
    /// How much longer a surface lasts at <paramref name="luminance"/> than at
    /// <paramref name="reference"/>. "Halving luminance more than triples life" (P2) is this
    /// function at <c>2^1.7 ≈ 3.25</c>.
    /// </summary>
    public static double LifetimeRatio(double luminance, double reference = 1.0,
                                       double exponent = DefaultExponent)
    {
        double rate = Rate(luminance, exponent);
        if (rate <= 0.0) return double.PositiveInfinity;
        return Rate(reference, exponent) / rate;
    }
}
