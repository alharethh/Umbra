using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Core.Color;
using Umbra.Core.Icons;

namespace Umbra.Shell.Wear;

/// <summary>
/// The light a drawn element emits, per source pixel, in linear relative luminance already
/// weighted by alpha.
/// </summary>
/// <remarks>
/// <para>
/// Alpha folds in here rather than at composite time because the bar's background is exactly black
/// (§4). Over black, straight alpha compositing in linear light is just <c>src × α</c>, and
/// luminance is linear in the linear channels, so the emitted luminance of a half-transparent
/// pixel is half its own. Anything drawn over a non-black background would need the real
/// compositing equation instead.
/// </para>
/// <para>
/// <see cref="Sample"/> is bilinear rather than nearest-neighbour, and that is load-bearing for
/// step 5 rather than a nicety. Micro-jitter works by moving content by fractions of a pixel so the
/// antialiasing gradient lands differently (§6.1); a nearest-neighbour sampler would quantise those
/// offsets away and report that sub-pixel motion changes nothing — which is precisely the question
/// §11.2 Q1 asks the accumulator to answer.
/// </para>
/// </remarks>
internal sealed class LuminanceField
{
    private readonly double[] _values;

    private LuminanceField(int width, int height, double[] values)
    {
        Width = width;
        Height = height;
        _values = values;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>A uniform field — a flat-coloured element such as the running indicator.</summary>
    public static LuminanceField Solid(Rgb colour) =>
        new(1, 1, [Srgb.RelativeLuminance(colour)]);

    /// <summary>Nothing lit at all.</summary>
    public static LuminanceField Dark { get; } = new(1, 1, [0.0]);

    /// <summary>Builds a field from a straight-alpha icon bitmap.</summary>
    public static LuminanceField FromIcon(IconBitmap icon)
    {
        ArgumentNullException.ThrowIfNull(icon);

        double[] values = new double[icon.Length];
        Span<Rgba> pixels = icon.Pixels;

        for (int i = 0; i < values.Length; i++)
        {
            Rgba pixel = pixels[i];
            if (pixel.A == 0) continue;

            values[i] = Srgb.RelativeLuminance(pixel.Rgb) * (pixel.A / 255.0);
        }

        return new LuminanceField(icon.Width, icon.Height, values);
    }

    /// <summary>Builds a field from a rendered icon.</summary>
    public static LuminanceField FromBitmap(BitmapSource source)
    {
        FormatConvertedBitmap converted = new(source, PixelFormats.Bgra32, null, 0);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        double[] values = new double[width * height];

        for (int i = 0; i < values.Length; i++)
        {
            int p = i * 4;
            byte alpha = pixels[p + 3];
            if (alpha == 0) continue;

            // Bgra32 arrives premultiplied, which is already src × α — exactly what is wanted.
            double linearB = Srgb.ToLinear(pixels[p + 0]);
            double linearG = Srgb.ToLinear(pixels[p + 1]);
            double linearR = Srgb.ToLinear(pixels[p + 2]);

            values[i] = Srgb.RelativeLuminance(linearR, linearG, linearB);
        }

        return new LuminanceField(width, height, values);
    }

    /// <summary>
    /// Bilinearly samples the field at normalised coordinates. Outside 0..1 reads as unlit.
    /// </summary>
    public double Sample(double u, double v)
    {
        if (u < 0.0 || u > 1.0 || v < 0.0 || v > 1.0) return 0.0;
        if (Width == 1 && Height == 1) return _values[0];

        double x = u * (Width - 1);
        double y = v * (Height - 1);

        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, Width - 1);
        int y1 = Math.Min(y0 + 1, Height - 1);

        double fx = x - x0;
        double fy = y - y0;

        double top = _values[y0 * Width + x0] * (1 - fx) + _values[y0 * Width + x1] * fx;
        double bottom = _values[y1 * Width + x0] * (1 - fx) + _values[y1 * Width + x1] * fx;

        return top * (1 - fy) + bottom * fy;
    }
}
