namespace Umbra.Core.Wear;

/// <summary>
/// The heatmap's colouring, as raw pixels (design-spec §9, phase 1).
/// </summary>
/// <remarks>
/// Here rather than in the shell so that the heatmap window, the picture it saves and
/// <c>tools/render-heatmap.cs</c> (the README's image) all colour a cell the same way. The tool
/// cannot reference the shell without pulling ManagedShell in, and it used to carry its own copy
/// of this ramp, which had already drifted from the window's by the time anyone compared them.
/// </remarks>
public static class HeatmapPixels
{
    /// <summary>How far below the peak the logarithmic scale reaches before it calls a cell black.</summary>
    public const double LogFloor = 1e-6;

    /// <summary>
    /// The accumulator as 32-bit BGRA, row-major, opaque, normalised to its own peak.
    /// </summary>
    /// <param name="accumulator">The dataset.</param>
    /// <param name="logarithmic">
    /// Compress the scale. The bar's dynamic range spans an unlit background at exactly zero and
    /// lit icon interiors several orders of magnitude above it, so a linear ramp renders as a few
    /// bright glyphs on black and hides everything the heatmap exists to show.
    /// </param>
    public static byte[] Bgra(WearAccumulator accumulator, bool logarithmic = true)
    {
        ArgumentNullException.ThrowIfNull(accumulator);

        double peak = accumulator.Peak;
        ReadOnlySpan<double> cells = accumulator.Cells;
        byte[] pixels = new byte[cells.Length * 4];

        for (int i = 0; i < cells.Length; i++)
        {
            double fraction = peak > 0.0 ? cells[i] / peak : 0.0;

            if (logarithmic && fraction > 0.0)
            {
                // Six decades below the peak, mapped onto 0..1.
                fraction = fraction < LogFloor
                    ? 0.0
                    : 1.0 - Math.Log10(fraction) / Math.Log10(LogFloor);
            }

            (byte r, byte g, byte b) = Ramp(Math.Clamp(fraction, 0.0, 1.0));

            int p = i * 4;
            pixels[p + 0] = b;
            pixels[p + 1] = g;
            pixels[p + 2] = r;
            pixels[p + 3] = 255;
        }

        return pixels;
    }

    /// <summary>
    /// Black, deep blue, magenta, orange, white: unlit stays black, so the image reads as the bar
    /// does. These are the README picture's stops, which is the one image people have seen.
    /// </summary>
    public static (byte R, byte G, byte B) Ramp(double t)
    {
        ReadOnlySpan<(double At, double R, double G, double B)> stops =
        [
            (0.00, 0, 0, 0),
            (0.25, 20, 24, 90),
            (0.50, 150, 30, 120),
            (0.75, 240, 120, 40),
            (1.00, 255, 255, 235),
        ];

        for (int i = 1; i < stops.Length; i++)
        {
            if (t > stops[i].At && i != stops.Length - 1) continue;

            var (a0, r0, g0, b0) = stops[i - 1];
            var (a1, r1, g1, b1) = stops[i];
            double f = a1 > a0 ? Math.Clamp((t - a0) / (a1 - a0), 0.0, 1.0) : 0.0;

            return ((byte)(r0 + (r1 - r0) * f),
                    (byte)(g0 + (g1 - g0) * f),
                    (byte)(b0 + (b1 - b0) * f));
        }

        return (0, 0, 0);
    }
}
