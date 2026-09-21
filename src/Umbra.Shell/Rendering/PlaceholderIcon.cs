using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Umbra.Shell.Rendering;

/// <summary>
/// What a slot draws when nothing can tell it what the application's icon is.
/// </summary>
/// <remarks>
/// <para>
/// <b>An icon that cannot be found used to draw nothing, and nothing reads as a bug.</b> A gap in
/// the row looks like a layout fault rather than a missing icon — the reported symptom was "a
/// weird logo to the right of firefox" for one such slot and simple puzzlement for others. A
/// placeholder is still wrong, but it is visibly a placeholder: it holds the slot, it takes the
/// hover chip and the running indicator like any other icon, and the tooltip still names the
/// application.
/// </para>
/// <para>
/// Drawn arithmetically rather than loaded, for two reasons. It needs to be producible on a worker
/// thread, where <c>RenderTargetBitmap</c> wants a dispatcher it may not have; and a rounded
/// outline is the one shape guaranteed to survive
/// <see cref="Umbra.Core.Icons.IconNormalizer"/> — it is all edge, so the P90 it normalises
/// against is the stroke itself and the result lands exactly on the tier's target luminance.
/// </para>
/// <para>
/// Stroke-only is also the cheaper choice under §2.3: at 48 px the outline lights about a tenth of
/// what a filled square would, and every slot that fails to resolve draws this for as long as it
/// is on the bar.
/// </para>
/// </remarks>
internal static class PlaceholderIcon
{
    private const int Extent = 48;
    private const double Corner = 9.0;
    private const double Inset = 7.0;
    private const double HalfStroke = 1.75;

    private static readonly Lazy<BitmapSource> Instance = new(Build);

    /// <summary>The placeholder, built once and frozen.</summary>
    public static BitmapSource Get() => Instance.Value;

    private static BitmapSource Build()
    {
        int stride = Extent * 4;
        byte[] pixels = new byte[stride * Extent];

        double half = Extent / 2.0;
        double box = half - Inset - Corner;

        for (int y = 0; y < Extent; y++)
        {
            for (int x = 0; x < Extent; x++)
            {
                // Signed distance to a rounded rectangle centred on the bitmap, then a soft band
                // either side of zero. Sampling the distance rather than rasterising a path is
                // what keeps this free of WPF and therefore usable from any thread.
                double px = Math.Abs(x + 0.5 - half);
                double py = Math.Abs(y + 0.5 - half);

                double dx = Math.Max(px - box, 0.0);
                double dy = Math.Max(py - box, 0.0);
                double distance = Math.Sqrt(dx * dx + dy * dy) - Corner;

                double coverage = 1.0 - Smoothstep(HalfStroke - 0.75, HalfStroke + 0.75, Math.Abs(distance));
                if (coverage <= 0.0) continue;

                byte alpha = (byte)Math.Clamp(coverage * 255.0, 0, 255);

                // Premultiplied Bgra32, white — the pipeline normalises the luminance, so what
                // matters here is only the shape and the alpha.
                int p = y * stride + x * 4;
                pixels[p + 0] = alpha;
                pixels[p + 1] = alpha;
                pixels[p + 2] = alpha;
                pixels[p + 3] = alpha;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            Extent, Extent, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);

        bitmap.Freeze();
        return bitmap;
    }

    private static double Smoothstep(double edge0, double edge1, double value)
    {
        double t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }
}
