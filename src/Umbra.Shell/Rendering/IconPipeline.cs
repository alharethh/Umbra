using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Core.Color;
using Umbra.Core.Icons;
using Umbra.Core.Tiers;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Turns an application's own icon into the appearance its tier calls for: monochrome and
/// normalised at rest, cross-fading to luminance-capped full chroma at Tier 2.
/// </summary>
/// <remarks>
/// <para>
/// Results are cached per source icon per quantised tier level. Regenerating 24 × 24 pixels is
/// cheap, but doing it for every icon on every animation frame is not, and the bar spends ~92% of
/// its life at a single level (Tier 0) where one cached bitmap serves indefinitely.
/// </para>
/// <para>
/// The Tier 1 → 2 chroma cross-fade is blended into the bitmap in linear light rather than done
/// with <c>Opacity</c> on a stacked pair of images. Opacity blends in sRGB values, which is the
/// same error §3.4 calls out for the proximity ramp — it would spend more of the transition bright
/// than intended, and the wear model reads what was actually emitted.
/// </para>
/// </remarks>
internal sealed class IconPipeline
{
    /// <summary>
    /// Tier levels are rounded to this many steps before rendering, so a smooth ramp reuses a
    /// small set of bitmaps. 64 steps over the 0..2 range is finer than 8-bit output can show.
    /// </summary>
    private const int LevelSteps = 64;

    private readonly Dictionary<(ImageSource Source, int Level), BitmapSource> _cache = [];

    /// <summary>Renders <paramref name="source"/> as it should appear at a continuous tier level.</summary>
    public BitmapSource? Render(ImageSource? source, BarElement element, double level)
    {
        if (source is not BitmapSource bitmap) return null;

        int quantised = (int)Math.Round(Math.Clamp(level, 0.0, 2.0) / 2.0 * LevelSteps);
        if (_cache.TryGetValue((source, quantised), out BitmapSource? cached)) return cached;

        BitmapSource rendered = Build(bitmap, element, quantised / (double)LevelSteps * 2.0);
        _cache[(source, quantised)] = rendered;
        return rendered;
    }

    /// <summary>Drops cached bitmaps. Call when the icon set changes materially.</summary>
    public void Clear() => _cache.Clear();

    /// <summary>Runs the §5.1 step 6 diagnostic over an icon, once.</summary>
    public void ReportOnce(string title, ImageSource? source)
    {
        if (source is BitmapSource bitmap) IconDiagnostics.Report(title, ToIconBitmap(bitmap));
    }

    private static BitmapSource Build(BitmapSource source, BarElement element, double level)
    {
        IconBitmap art = ToIconBitmap(source);

        TierRender appearance = TierPalette.Resolve(element, level);

        // The monochrome layer, normalised so its P90 lands on the tier's target luminance.
        double target = Srgb.RelativeLuminance(appearance.Monochrome);
        IconBitmap result = IconNormalizer.Prepare(art, target, monochrome: true);

        if (appearance.ChromaBlend > 0.0)
        {
            IconBitmap chroma = IconNormalizer.CapTo(art, TierPalette.Tier2LuminanceCap);
            result = BlendInLinearLight(result, chroma, appearance.ChromaBlend);
        }

        BitmapSource output = ToBitmapSource(result);
        output.Freeze();
        return output;
    }

    private static IconBitmap BlendInLinearLight(IconBitmap from, IconBitmap to, double t)
    {
        IconBitmap result = new(from.Width, from.Height);
        Span<Rgba> a = from.Pixels;
        Span<Rgba> b = to.Pixels;
        Span<Rgba> output = result.Pixels;

        for (int i = 0; i < a.Length; i++)
        {
            Rgb blended = Srgb.Lerp(a[i].Rgb, b[i].Rgb, t);
            output[i] = new Rgba(blended.R, blended.G, blended.B, a[i].A);
        }

        return result;
    }

    private static IconBitmap ToIconBitmap(BitmapSource source)
    {
        FormatConvertedBitmap converted = new(source, PixelFormats.Bgra32, null, 0);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        IconBitmap icon = new(width, height);
        Span<Rgba> target = icon.Pixels;

        for (int i = 0; i < target.Length; i++)
        {
            int p = i * 4;
            // Bgra32 from WPF is premultiplied by alpha; undo it so the colour maths sees the
            // straight values, or every partially transparent edge pixel reads as too dark and
            // drags the percentile down with it.
            byte alpha = pixels[p + 3];
            target[i] = alpha == 0
                ? Rgba.Transparent
                : new Rgba(
                    Unpremultiply(pixels[p + 2], alpha),
                    Unpremultiply(pixels[p + 1], alpha),
                    Unpremultiply(pixels[p + 0], alpha),
                    alpha);
        }

        return icon;
    }

    private static byte Unpremultiply(byte channel, byte alpha) =>
        (byte)Math.Clamp(channel * 255 / alpha, 0, 255);

    private static BitmapSource ToBitmapSource(IconBitmap icon)
    {
        int stride = icon.Width * 4;
        byte[] pixels = new byte[stride * icon.Height];
        Span<Rgba> source = icon.Pixels;

        for (int i = 0; i < source.Length; i++)
        {
            Rgba pixel = source[i];
            int p = i * 4;
            // Back to premultiplied Bgra32 for WPF.
            pixels[p + 0] = Premultiply(pixel.B, pixel.A);
            pixels[p + 1] = Premultiply(pixel.G, pixel.A);
            pixels[p + 2] = Premultiply(pixel.R, pixel.A);
            pixels[p + 3] = pixel.A;
        }

        return BitmapSource.Create(
            icon.Width, icon.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
    }

    private static byte Premultiply(byte channel, byte alpha) =>
        (byte)Math.Clamp(channel * alpha / 255, 0, 255);
}
