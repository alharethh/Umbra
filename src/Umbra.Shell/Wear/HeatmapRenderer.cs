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
        byte[] pixels = HeatmapPixels.Bgra(accumulator, logarithmic);

        BitmapSource bitmap = BitmapSource.Create(
            width, accumulator.Height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
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
