using System.Windows;

namespace Umbra.Shell.Wear;

/// <summary>
/// Rasterises a described scene into a buffer of emitted luminance, one cell per physical pixel of
/// the reserved rectangle.
/// </summary>
/// <remarks>
/// This is the step that decides whether the whole wear dataset means anything, so it is kept
/// separate from the timer that calls it and tested on its own. In particular it must stay
/// sensitive to sub-pixel placement: §11.2 Q1 asks whether the motion layer earns its complexity,
/// and micro-jitter moves content by fractions of a pixel (§6.1). A compositor that snapped
/// emitters to integer cells would answer "no" by construction, and the answer would look
/// perfectly credible.
/// </remarks>
internal static class SceneCompositor
{
    /// <summary>
    /// Composites <paramref name="emitters"/> into <paramref name="frame"/>, converting bounds from
    /// device-independent to physical pixels by <paramref name="dpiScale"/>.
    /// </summary>
    public static void Composite(
        IReadOnlyList<WearEmitter> emitters, double[] frame, int width, int height, double dpiScale)
    {
        ArgumentNullException.ThrowIfNull(emitters);
        ArgumentNullException.ThrowIfNull(frame);

        Array.Clear(frame);

        double scale = dpiScale > 0.0 ? dpiScale : 1.0;

        foreach (WearEmitter emitter in emitters)
        {
            Rect bounds = emitter.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;

            double left = bounds.Left * scale;
            double top = bounds.Top * scale;
            double right = bounds.Right * scale;
            double bottom = bounds.Bottom * scale;

            double spanX = right - left;
            double spanY = bottom - top;
            if (spanX <= 0 || spanY <= 0) continue;

            int x0 = Math.Max(0, (int)Math.Floor(left));
            int y0 = Math.Max(0, (int)Math.Floor(top));
            int x1 = Math.Min(width - 1, (int)Math.Ceiling(right));
            int y1 = Math.Min(height - 1, (int)Math.Ceiling(bottom));

            for (int y = y0; y <= y1; y++)
            {
                // Sampled at pixel centres against the emitter's true fractional bounds. This is
                // what keeps a sub-pixel shift visible: normalising against the integer cell range
                // instead would discard the fraction, and the accumulator would report that
                // micro-jitter changes nothing — answering §11.2 Q1 by construction.
                double v = (y + 0.5 - top) / spanY;
                if (v < 0.0 || v > 1.0) continue;

                for (int x = x0; x <= x1; x++)
                {
                    double u = (x + 0.5 - left) / spanX;
                    if (u < 0.0 || u > 1.0) continue;

                    double emitted = emitter.Field.Sample(u, v);
                    int index = y * width + x;

                    // Nothing in the bar overlaps today, but the brighter of the two is the safe
                    // reading of "what the panel had to emit here" if anything ever does.
                    if (emitted > frame[index]) frame[index] = emitted;
                }
            }
        }
    }
}
