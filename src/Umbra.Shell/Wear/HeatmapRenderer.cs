using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Core.Wear;

namespace Umbra.Shell.Wear;

/// <summary>Renders an accumulator as a heatmap (design-spec §9, phase 1).</summary>
internal static class HeatmapRenderer
{
    /// <summary>
    /// Renders the accumulator, normalised to its own peak.
    /// </summary>
    /// <param name="accumulator">The dataset.</param>
    /// <param name="logarithmic">
    /// Compress the scale. The bar's dynamic range spans an unlit background at exactly zero and
    /// lit icon interiors several orders of magnitude above it, so a linear ramp renders as a few
    /// bright glyphs on black and hides everything the heatmap exists to show.
    /// </param>
    public static BitmapSource Render(WearAccumulator accumulator, bool logarithmic = true)
    {
        ArgumentNullException.ThrowIfNull(accumulator);

        int width = accumulator.Width;
        int height = accumulator.Height;
        double peak = accumulator.Peak;

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];

        if (peak > 0.0)
        {
            ReadOnlySpan<double> cells = accumulator.Cells;

            for (int i = 0; i < cells.Length; i++)
            {
                double fraction = cells[i] / peak;

                if (logarithmic && fraction > 0.0)
                {
                    // Six decades below the peak, mapped onto 0..1.
                    const double floor = 1e-6;
                    fraction = fraction < floor
                        ? 0.0
                        : 1.0 - Math.Log10(fraction) / Math.Log10(floor);
                }

                (byte r, byte g, byte b) = Ramp(Math.Clamp(fraction, 0.0, 1.0));

                int p = i * 4;
                pixels[p + 0] = b;
                pixels[p + 1] = g;
                pixels[p + 2] = r;
                pixels[p + 3] = 255;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A dark-to-hot ramp: unlit stays black so the reserved rectangle reads as empty.</summary>
    private static (byte R, byte G, byte B) Ramp(double t)
    {
        (double At, double R, double G, double B)[] stops =
        [
            (0.00, 0, 0, 0),
            (0.25, 30, 40, 120),
            (0.50, 150, 40, 110),
            (0.75, 240, 110, 40),
            (1.00, 255, 245, 200),
        ];

        for (int i = 1; i < stops.Length; i++)
        {
            if (t > stops[i].At && i != stops.Length - 1) continue;

            var (a0, r0, g0, b0) = stops[i - 1];
            var (a1, r1, g1, b1) = stops[i];
            double f = a1 > a0 ? (t - a0) / (a1 - a0) : 0.0;
            f = Math.Clamp(f, 0.0, 1.0);

            return ((byte)(r0 + (r1 - r0) * f),
                    (byte)(g0 + (g1 - g0) * f),
                    (byte)(b0 + (b1 - b0) * f));
        }

        return (0, 0, 0);
    }

    /// <summary>
    /// The figures worth reading alongside the picture. Deliberately blunt about what the model
    /// cannot tell you (§9, "stated limits").
    /// </summary>
    public static string Summarise(WearAccumulator accumulator)
    {
        ArgumentNullException.ThrowIfNull(accumulator);

        double hours = accumulator.ElapsedSeconds / 3600.0;
        double litFraction = 0.0;

        ReadOnlySpan<double> cells = accumulator.Cells;
        int lit = 0;
        foreach (double cell in cells) if (cell > 0.0) lit++;
        if (cells.Length > 0) litFraction = lit / (double)cells.Length;

        return string.Create(CultureInfo.InvariantCulture,
            $"""
             {accumulator.Width} × {accumulator.Height} px over {hours:F2} h of wall clock
             exponent n = {accumulator.Exponent}

             peak cell   {accumulator.Peak:G6}
             mean cell   {accumulator.Mean:G6}
             total       {accumulator.Total:G6}
             lit area    {litFraction:P2} of the reserved rectangle

             Relative, not absolute. This ranks wear within the bar and has no calibration
             against actual panel degradation. It is blind to the panel's own compensation
             cycles, and it ignores the rest of the screen — which per §2.4 very likely ages
             faster than the bar does.
             """);
    }
}
