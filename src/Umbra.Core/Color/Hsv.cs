namespace Umbra.Core.Color;

/// <summary>
/// A colour as hue, saturation and value, the three axes a colour picker drags along.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hue is in degrees, 0 up to but not including 360; saturation and value are 0 to 1.</b>
/// These are sRGB-encoded values, not linear light: a picker's square is laid out in what the
/// eye sees as even steps, and nothing about wear is decided here. What the bar actually draws is
/// still capped and tinted in linear light afterwards.
/// </para>
/// <para>
/// A grey has no hue and black has no saturation either, so <see cref="FromRgb"/> reports 0 for
/// them. A picker therefore keeps its own hue while the user drags into the grey column, rather
/// than reading it back from the colour, or the hue strip would jump to red.
/// </para>
/// </remarks>
public readonly record struct Hsv(double H, double S, double V)
{
    /// <summary>The axes of an sRGB colour; a grey reports hue 0.</summary>
    public static Hsv FromRgb(Rgb colour)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == r) h = 60 * (((g - b) / delta) % 6);
            else if (max == g) h = 60 * ((b - r) / delta + 2);
            else h = 60 * ((r - g) / delta + 4);
        }
        if (h < 0) h += 360;

        double s = max <= 0 ? 0 : delta / max;
        return new Hsv(h, s, max);
    }

    /// <summary>The sRGB colour, with the hue wrapped and the other two clamped to 0..1.</summary>
    public Rgb ToRgb()
    {
        double h = ((H % 360) + 360) % 360;
        double s = Math.Clamp(S, 0, 1);
        double v = Math.Clamp(V, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        static byte Byte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0, 1) * 255);
        return new Rgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }
}
