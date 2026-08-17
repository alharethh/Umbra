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
    /// <param name="edgeStrength">
    /// How strong an internal boundary must be to count, as a fraction of the icon's own strongest
    /// gradient. Relative rather than absolute so low-contrast artwork is not simply ignored.
    /// </param>
    /// <param name="alphaThreshold">How much alpha must change to count as a silhouette edge.</param>
    /// <param name="thickness">
    /// Stroke half-width in <i>source</i> pixels. §5.3 asks for 1.5 px of stroke on screen, which
    /// depends on the source resolution against the drawn size — a 32 px source drawn at 24 px
    /// needs a thicker source stroke than a 24 px one does.
    /// </param>
    public static IconBitmap ToOutline(
        IconBitmap icon,
        double edgeStrength = 0.10,
        int alphaThreshold = 32,
        int thickness = 1)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentOutOfRangeException.ThrowIfNegative(thickness);

        bool[] edges = Silhouette(icon, alphaThreshold);
        bool[] interior = InteriorEdges(icon, edgeStrength);

        for (int i = 0; i < edges.Length; i++) edges[i] |= interior[i];

        if (thickness > 1) edges = Dilate(edges, icon.Width, icon.Height, thickness - 1);

        IconBitmap result = new(icon.Width, icon.Height);
        Span<Rgba> source = icon.Pixels;
        Span<Rgba> target = result.Pixels;

        for (int i = 0; i < source.Length; i++)
            if (edges[i]) target[i] = source[i];

        return result;
    }

    /// <summary>
    /// The alpha silhouette. This one genuinely is a step change — a pixel is in the shape or it is
    /// not — so a plain threshold is the right tool and needs none of the machinery below.
    /// </summary>
    private static bool[] Silhouette(IconBitmap icon, int alphaThreshold)
    {
        int width = icon.Width;
        int height = icon.Height;
        Span<Rgba> pixels = icon.Pixels;
        bool[] edges = new bool[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (pixels[index].A == 0) continue;

                for (int dy = -1; dy <= 1 && !edges[index]; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;

                        int nx = x + dx;
                        int ny = y + dy;

                        // Off the artwork counts as transparent, so a full-bleed icon still gets
                        // its outermost ring detected.
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            edges[index] = true;
                            break;
                        }

                        if (Math.Abs(pixels[index].A - pixels[ny * width + nx].A) >= alphaThreshold)
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

    /// <summary>
    /// Internal luminance edges, found as <i>ridges</i> in the gradient rather than as any place
    /// the gradient is non-zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The naive version — flag a pixel whenever a neighbour differs by more than some threshold —
    /// works on flat-shaded artwork and falls apart on anything with a gradient in it. A smooth
    /// ramp crosses the threshold at every step, so the whole ramp is reported as edge and the icon
    /// comes back as bands and speckle instead of lines. That was the first implementation and it
    /// looked exactly as bad as that description suggests.
    /// </para>
    /// <para>
    /// The fix is non-maximum suppression: blur slightly, take the Sobel gradient, and keep a pixel
    /// only where its gradient magnitude is a local maximum <i>along the gradient direction</i>.
    /// A real boundary is a ridge and survives; a smooth ramp has no ridge — its magnitude is
    /// roughly constant — so almost nothing survives, which is the desired answer. Hysteresis then
    /// keeps weak pixels only where they connect to strong ones, so lines stay joined up without
    /// admitting isolated noise.
    /// </para>
    /// </remarks>
    private static bool[] InteriorEdges(IconBitmap icon, double edgeStrength)
    {
        int width = icon.Width;
        int height = icon.Height;

        if (width < 3 || height < 3) return new bool[width * height];

        double[] emitted = new double[width * height];
        Span<Rgba> pixels = icon.Pixels;

        for (int i = 0; i < emitted.Length; i++)
        {
            Rgba pixel = pixels[i];
            emitted[i] = pixel.A == 0 ? 0.0 : Srgb.RelativeLuminance(pixel.Rgb) * (pixel.A / 255.0);
        }

        double[] smoothed = Blur(emitted, width, height);
        double[] magnitude = new double[width * height];
        double[] angleX = new double[width * height];
        double[] angleY = new double[width * height];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;

                double gx =
                    -smoothed[i - width - 1] + smoothed[i - width + 1]
                    - 2 * smoothed[i - 1] + 2 * smoothed[i + 1]
                    - smoothed[i + width - 1] + smoothed[i + width + 1];

                double gy =
                    -smoothed[i - width - 1] - 2 * smoothed[i - width] - smoothed[i - width + 1]
                    + smoothed[i + width - 1] + 2 * smoothed[i + width] + smoothed[i + width + 1];

                magnitude[i] = Math.Sqrt(gx * gx + gy * gy);
                angleX[i] = gx;
                angleY[i] = gy;
            }
        }

        // Suppress everything that is not a ridge crest.
        double[] thinned = new double[width * height];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                if (magnitude[i] <= 0.0) continue;

                // Quantise the gradient direction to the nearest of four axes and compare against
                // the two neighbours that lie along it.
                double gx = angleX[i];
                double gy = angleY[i];
                double absX = Math.Abs(gx);
                double absY = Math.Abs(gy);

                int stepX, stepY;

                if (absX >= 2.414 * absY) { stepX = 1; stepY = 0; }
                else if (absY >= 2.414 * absX) { stepX = 0; stepY = 1; }
                else { stepX = 1; stepY = gx * gy >= 0 ? 1 : -1; }

                double before = magnitude[i - stepY * width - stepX];
                double after = magnitude[i + stepY * width + stepX];

                // Strictly greater on one side, not both. With >= on both, a constant-gradient ramp
                // is a plateau where every pixel ties with its neighbours and every pixel survives
                // — which is the banding this whole function exists to remove. Breaking the tie on
                // one side thins a plateau to its leading edge and leaves real crests intact.
                if (magnitude[i] > before && magnitude[i] >= after) thinned[i] = magnitude[i];
            }
        }

        return Hysteresis(thinned, width, height, edgeStrength);
    }

    private static double[] Blur(double[] source, int width, int height)
    {
        double[] result = new double[source.Length];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double sum = 0.0;
                double weight = 0.0;

                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

                        double w = (dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1);
                        sum += source[ny * width + nx] * w;
                        weight += w;
                    }
                }

                result[y * width + x] = weight > 0 ? sum / weight : 0.0;
            }
        }

        return result;
    }

    /// <summary>
    /// Two thresholds: strong pixels are edges outright, weak ones only where they touch a strong
    /// one. Keeps boundaries continuous without letting isolated noise through.
    /// </summary>
    private static bool[] Hysteresis(double[] magnitude, int width, int height, double edgeStrength)
    {
        double peak = 0.0;
        foreach (double value in magnitude) if (value > peak) peak = value;

        bool[] edges = new bool[magnitude.Length];

        // An absolute floor as well as a relative one. A purely relative threshold has no defence
        // against a flat icon: there the strongest "gradient" is floating-point dust from the blur,
        // around 1e-17, and a fraction of that admits every pixel in the image. A Sobel step across
        // a real boundary of ΔL is roughly 4ΔL, so this floor corresponds to boundaries of about
        // half a percent in linear luminance — below that there is no internal structure to find.
        const double MinimumGradient = 0.02;

        if (peak <= MinimumGradient) return edges;

        // Relative to the icon's own strongest edge, so low-contrast artwork is not simply ignored
        // and high-contrast artwork does not light up everywhere.
        double high = Math.Max(peak * edgeStrength, MinimumGradient);
        double low = high * 0.4;

        Queue<int> pending = new();

        for (int i = 0; i < magnitude.Length; i++)
        {
            if (magnitude[i] < high) continue;

            edges[i] = true;
            pending.Enqueue(i);
        }

        while (pending.Count > 0)
        {
            int index = pending.Dequeue();
            int x = index % width;
            int y = index / width;

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

                    int neighbour = ny * width + nx;
                    if (edges[neighbour] || magnitude[neighbour] < low) continue;

                    edges[neighbour] = true;
                    pending.Enqueue(neighbour);
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
