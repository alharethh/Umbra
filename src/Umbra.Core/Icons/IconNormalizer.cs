using Umbra.Core.Color;

namespace Umbra.Core.Icons;

/// <summary>
/// Monochrome normalisation and the integrated-luminance diagnostic of design-spec §5.1.
/// </summary>
/// <remarks>
/// <para>
/// Naive desaturation fails badly across a real pin set — a dark logo vanishes, a white logo
/// blasts. Normalising to a percentile fixes that: <see cref="Percentile"/> is used rather than
/// the maximum (immune to a single stray bright pixel) and rather than the mean (immune to how
/// much padding the icon happens to carry).
/// </para>
/// <para>
/// <b>What this does not do.</b> v1 claimed P90 normalisation "makes the wear budget predictable
/// per slot". It does not, and the claim is withdrawn. P90 equalises <i>peak</i> brightness, not
/// emitted energy: a solid-fill logo and a thin-stroke logo land at identical P90 while differing
/// 5–10× in lit area, and therefore in wear. What it delivers is uniform perceived brightness
/// across the row, which is worth having on its own merits. Per-slot wear parity needs
/// <see cref="IntegratedLuminance"/>, and the honest fix for an offender is outline mode (§5.3),
/// which removes the interior rather than dimming the whole icon.
/// </para>
/// </remarks>
public static class IconNormalizer
{
    /// <summary>The normalisation percentile, 0.90 (§5.1 step 3).</summary>
    public const double Percentile = 0.90;

    /// <summary>
    /// Integrated luminance above this multiple of the row median is flagged (§5.1 step 6).
    /// </summary>
    public const double DiagnosticThreshold = 2.0;

    /// <summary>
    /// Percentile of per-pixel luminance over non-transparent pixels, by nearest rank.
    /// </summary>
    /// <remarks>
    /// Alpha weights whether a pixel counts, not how much: a pixel is in the sample or it is not.
    /// Returns 0 when every pixel is transparent.
    /// </remarks>
    public static double PercentileLuminance(IconBitmap icon, double percentile = Percentile,
                                             byte alphaThreshold = 0)
    {
        ArgumentNullException.ThrowIfNull(icon);
        if (percentile is <= 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be in (0, 1].");

        List<double> sample = [];
        foreach (Rgba pixel in icon.Pixels)
            if (pixel.A > alphaThreshold)
                sample.Add(Srgb.RelativeLuminance(pixel.Rgb));

        if (sample.Count == 0) return 0.0;

        sample.Sort();
        int rank = (int)Math.Ceiling(percentile * sample.Count) - 1;
        return sample[Math.Clamp(rank, 0, sample.Count - 1)];
    }

    /// <summary>
    /// Scales an icon in linear light so its <see cref="Percentile"/> luminance lands on
    /// <paramref name="targetLuminance"/> — the tier's target for that element.
    /// </summary>
    /// <remarks>
    /// <b>Alpha is copied through untouched.</b> The silhouette is what carries recognition at
    /// this size, and clipping it would cost more than any wear it saved.
    /// </remarks>
    public static IconBitmap Normalise(IconBitmap icon, double targetLuminance,
                                       byte alphaThreshold = 0)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentOutOfRangeException.ThrowIfNegative(targetLuminance);

        double reference = PercentileLuminance(icon, Percentile, alphaThreshold);
        if (reference <= 0.0) return icon.Clone();

        return Scale(icon, targetLuminance / reference);
    }

    /// <summary>Scales every pixel's linear light by <paramref name="factor"/>, clamping to gamut.</summary>
    public static IconBitmap Scale(IconBitmap icon, double factor)
    {
        ArgumentNullException.ThrowIfNull(icon);

        IconBitmap result = new(icon.Width, icon.Height);
        Span<Rgba> source = icon.Pixels;
        Span<Rgba> target = result.Pixels;

        for (int i = 0; i < source.Length; i++)
        {
            Rgba pixel = source[i];
            Rgb scaled = Srgb.Scale(pixel.Rgb, factor);
            target[i] = new Rgba(scaled.R, scaled.G, scaled.B, pixel.A);
        }

        return result;
    }

    /// <summary>
    /// Total linear luminance over the icon, weighted by alpha — the energy proxy that P90 does
    /// not equalise, and what actually predicts per-slot wear.
    /// </summary>
    public static double IntegratedLuminance(IconBitmap icon)
    {
        ArgumentNullException.ThrowIfNull(icon);

        double total = 0.0;
        foreach (Rgba pixel in icon.Pixels)
            if (pixel.A > 0)
                total += Srgb.RelativeLuminance(pixel.Rgb) * (pixel.A / 255.0);

        return total;
    }

    /// <summary>Median of a set of integrated luminances. Returns 0 for an empty set.</summary>
    public static double Median(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        List<double> sorted = [.. values];
        if (sorted.Count == 0) return 0.0;

        sorted.Sort();
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>
    /// The §5.1 step 6 diagnostic: how many times the row median this icon costs.
    /// </summary>
    /// <remarks>
    /// Ship this as a diagnostic first — surface "this icon costs 4× the row median" in settings
    /// and let the user decide. Automatic dimming of specific logos will look like a rendering bug.
    /// </remarks>
    public static double CostRelativeToRow(IconBitmap icon, double rowMedianIntegratedLuminance)
    {
        ArgumentNullException.ThrowIfNull(icon);
        if (rowMedianIntegratedLuminance <= 0.0) return 0.0;
        return IntegratedLuminance(icon) / rowMedianIntegratedLuminance;
    }

    /// <summary>True when an icon exceeds the row median by more than <see cref="DiagnosticThreshold"/>.</summary>
    public static bool IsFlagged(IconBitmap icon, double rowMedianIntegratedLuminance,
                                 double threshold = DiagnosticThreshold) =>
        CostRelativeToRow(icon, rowMedianIntegratedLuminance) > threshold;
}
