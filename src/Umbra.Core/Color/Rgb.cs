using System.Globalization;

namespace Umbra.Core.Color;

/// <summary>An 8-bit sRGB-encoded colour. Encoded values only — see <see cref="Srgb"/> for linear light.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>The background, per design-spec §4. Exactly black — not <c>#010101</c>, not near-black.</summary>
    public static Rgb Black => new(0, 0, 0);

    /// <summary>Panel peak.</summary>
    public static Rgb White => new(255, 255, 255);

    /// <summary>Parses <c>#RRGGBB</c> or <c>RRGGBB</c>. Case-insensitive, culture-independent.</summary>
    public static Rgb FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length != 6) throw new FormatException($"Expected 6 hex digits, got \"{hex}\".");

        return new Rgb(ParseByte(s[..2]), ParseByte(s[2..4]), ParseByte(s[4..]));

        static byte ParseByte(ReadOnlySpan<char> pair) =>
            byte.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>Renders as <c>#RRGGBB</c>, upper case.</summary>
    public string ToHex() => string.Create(
        CultureInfo.InvariantCulture,
        $"#{R:X2}{G:X2}{B:X2}");

    /// <inheritdoc/>
    public override string ToString() => ToHex();
}

/// <summary>An 8-bit sRGB colour with a straight (non-premultiplied) alpha channel.</summary>
/// <remarks>
/// Alpha is the silhouette, and per design-spec §5.1 step 5 the silhouette is what carries
/// recognition at taskbar size. Nothing in this library modifies it.
/// </remarks>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    /// <summary>A fully transparent pixel. Excluded from every icon statistic.</summary>
    public static Rgba Transparent => default;

    /// <summary>Constructs an opaque pixel from an <see cref="Rgb"/>.</summary>
    public Rgba(Rgb colour) : this(colour.R, colour.G, colour.B, 255) { }

    /// <summary>The colour channels without alpha.</summary>
    public Rgb Rgb => new(R, G, B);
}
