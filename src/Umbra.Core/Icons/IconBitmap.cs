using Umbra.Core.Color;

namespace Umbra.Core.Icons;

/// <summary>A straight-alpha RGBA bitmap. Deliberately free of any imaging framework.</summary>
public sealed class IconBitmap
{
    private readonly Rgba[] _pixels;

    /// <summary>Creates a fully transparent bitmap.</summary>
    public IconBitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        _pixels = new Rgba[checked(width * height)];
    }

    /// <summary>Creates a bitmap over existing row-major pixels, which are copied.</summary>
    public IconBitmap(int width, int height, ReadOnlySpan<Rgba> pixels) : this(width, height)
    {
        if (pixels.Length != _pixels.Length)
            throw new ArgumentException(
                $"Expected {_pixels.Length} pixels for {width}×{height}, got {pixels.Length}.",
                nameof(pixels));
        pixels.CopyTo(_pixels);
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Total pixel count.</summary>
    public int Length => _pixels.Length;

    /// <summary>Pixels, row-major.</summary>
    public Span<Rgba> Pixels => _pixels;

    /// <summary>Indexed access.</summary>
    public Rgba this[int x, int y]
    {
        get => _pixels[Index(x, y)];
        set => _pixels[Index(x, y)] = value;
    }

    /// <summary>An independent copy.</summary>
    public IconBitmap Clone() => new(Width, Height, _pixels);

    private int Index(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return y * Width + x;
    }
}
