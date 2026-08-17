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
    /// Step 2 of §5.1: replaces every pixel with a neutral grey of the same relative luminance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neutral is not a compromise here, it is the efficient path (P4): on a WRGB panel a grey is
    /// carried predominantly by the unfiltered white emitter, whereas saturated colour requires
    /// filtering white light and discarding most of it, driving the emitter harder for the same
    /// luminance. Note the wear model in <see cref="Wear.WearModel"/> does not capture that
    /// difference — it sees only luminance — so this is a panel-level argument, not something the
    /// heatmap will show.
    /// </para>
    /// <para>
    /// Alpha is preserved, because the silhouette is what carries recognition once the colour is
    /// gone.
    /// </para>
    /// </remarks>
    public static IconBitmap ToMonochrome(IconBitmap icon)
    {
        ArgumentNullException.ThrowIfNull(icon);

        IconBitmap result = new(icon.Width, icon.Height);
        Span<Rgba> source = icon.Pixels;
        Span<Rgba> target = result.Pixels;

        for (int i = 0; i < source.Length; i++)
        {
            Rgba pixel = source[i];
            byte grey = Srgb.ToByte(Srgb.RelativeLuminance(pixel.Rgb));
            target[i] = new Rgba(grey, grey, grey, pixel.A);
        }

        return result;
    }

    /// <summary>
    /// The §5.1 pipeline for a rest tier: desaturate, then scale so the <see cref="Percentile"/>
    /// luminance lands on <paramref name="targetLuminance"/>.
    /// </summary>
    /// <remarks>
    /// Order matters. Desaturating first and normalising second lands the <i>grey</i> icon's P90 on
    /// the target; normalising first would land the colour icon's P90 there and then move it again
    /// when the colours collapse to their luminances. A neutral grey can always reach any target
    /// below white without clipping, which is why this direction is safe and
    /// <see cref="CapTo"/> is not.
    /// </remarks>
    public static IconBitmap Prepare(IconBitmap icon, double targetLuminance, bool monochrome)
    {
        ArgumentNullException.ThrowIfNull(icon);

        return Normalise(monochrome ? ToMonochrome(icon) : icon, targetLuminance);
    }

    /// <summary>
    /// The §3.2 Tier 2 treatment: tone-map the source artwork down so its <see cref="Percentile"/>
    /// luminance lands at or below <paramref name="cap"/>, leaving hue and saturation untouched.
    /// Icons already below the cap are returned unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Downward only, and that is not a shortcut.</b> §3.2 specifies a cap, not a target, and
    /// scaling a saturated colour <i>up</i> to a luminance its gamut cannot reach clips the
    /// dominant channel — which both undershoots the target and distorts the hue the cap exists to
    /// preserve. A pure red tops out at L = 0.2126 however hard it is driven; asking it for 0.45
    /// yields 0.287 and a visibly different red.
    /// </para>
    /// <para>
    /// So a dim icon stays dim at Tier 2. That is the correct behaviour: the cap is a ceiling on
    /// what an icon may cost, not a brightness every icon must be brought up to.
    /// </para>
    /// </remarks>
    public static IconBitmap CapTo(IconBitmap icon, double cap)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentOutOfRangeException.ThrowIfNegative(cap);

        double peak = PercentileLuminance(icon);
        if (peak <= cap || peak <= 0.0) return icon.Clone();

        return Scale(icon, cap / peak);
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
    /// Outline mode (§5.3): keep the icon's edges, discard its fill.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under P5 this is the <b>primary</b> defence for icon interiors, not an optional extra.
    /// Slot-bounded drift cannot help there — meaningful interior reduction needs an amplitude
    /// greater than the icon's own width, which is nearly three times the whole gutter — so the
    /// only thing that reduces interior wear is removing the interior.
    /// </para>
    /// <para>
    /// Edges come from two sources, per §5.3: the alpha silhouette, and internal luminance
    /// boundaries. A solid-fill logo therefore keeps its outline and any internal shapes, and loses
    /// the flat regions between them.
    /// </para>
    /// <para>
    /// <b>Whether the result looks acceptable is not settled</b> — spec §11.2 Q10 asks exactly that
    /// and expects it may need hand-authored glyphs for common apps. This produces the automatic
    /// version so the question can be answered by looking at a real pin set. It stays a per-icon
    /// opt-in either way; applying it to everything by default would cost recognisability unevenly.
    /// </para>
    /// </remarks>
    /// <param name="icon">The source artwork.</param>
    /// <param name="luminanceThreshold">
    /// How much linear luminance must change between neighbours to count as an internal edge.
    /// </param>
    /// <param name="alphaThreshold">How much alpha must change to count as a silhouette edge.</param>
    /// <param name="thickness">
    /// Stroke half-width in <i>source</i> pixels. §5.3 asks for 1.5 px of stroke on screen, which
    /// depends on the source resolution against the drawn size — a 32 px source drawn at 24 px
    /// needs a thicker source stroke than a 24 px one does.
    /// </param>
    public static IconBitmap ToOutline(
        IconBitmap icon,
        double luminanceThreshold = 0.02,
        int alphaThreshold = 32,
        int thickness = 1)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentOutOfRangeException.ThrowIfNegative(thickness);

        bool[] edges = DetectEdges(icon, luminanceThreshold, alphaThreshold);
        if (thickness > 1) edges = Dilate(edges, icon.Width, icon.Height, thickness - 1);

        IconBitmap result = new(icon.Width, icon.Height);
        Span<Rgba> source = icon.Pixels;
        Span<Rgba> target = result.Pixels;

        for (int i = 0; i < source.Length; i++)
            if (edges[i]) target[i] = source[i];

        return result;
    }

    private static bool[] DetectEdges(IconBitmap icon, double luminanceThreshold, int alphaThreshold)
    {
        int width = icon.Width;
        int height = icon.Height;

        double[] emitted = new double[width * height];
        int[] alpha = new int[width * height];
        Span<Rgba> pixels = icon.Pixels;

        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba pixel = pixels[i];
            alpha[i] = pixel.A;
            emitted[i] = pixel.A == 0 ? 0.0 : Srgb.RelativeLuminance(pixel.Rgb);
        }

        bool[] edges = new bool[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (alpha[index] == 0) continue;          // transparent pixels emit nothing anyway

                for (int dy = -1; dy <= 1 && !edges[index]; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;

                        int nx = x + dx;
                        int ny = y + dy;

                        // Off the edge of the artwork counts as transparent, so the outermost ring
                        // of a full-bleed icon is still detected as its silhouette.
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            edges[index] = true;
                            break;
                        }

                        int neighbour = ny * width + nx;

                        if (Math.Abs(alpha[index] - alpha[neighbour]) >= alphaThreshold ||
                            Math.Abs(emitted[index] - emitted[neighbour]) >= luminanceThreshold)
                        {
                            edges[index] = true;
                            break;
                        }
                    }
                }
            }
        }

        return edges;
    }

    private static bool[] Dilate(bool[] mask, int width, int height, int radius)
    {
        bool[] result = new bool[mask.Length];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!mask[y * width + x]) continue;

                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

                        result[ny * width + nx] = true;
                    }
                }
            }
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
