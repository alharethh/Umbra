namespace Umbra.Core.Layout;

/// <summary>
/// An axis-aligned rectangle in device-independent screen coordinates: origin top-left, x right,
/// y down.
/// </summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    /// <summary>Left edge.</summary>
    public double Left => X;

    /// <summary>Top edge.</summary>
    public double Top => Y;

    /// <summary>Right edge.</summary>
    public double Right => X + Width;

    /// <summary>Bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>Centre point.</summary>
    public (double X, double Y) Center => (X + Width / 2.0, Y + Height / 2.0);

    /// <summary>Translates by an offset.</summary>
    public Rect Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>
    /// True when <paramref name="inner"/> lies entirely within this rectangle. This is how P5 is
    /// checked: a drawn rect must always be contained by its own hit slot.
    /// </summary>
    public bool Contains(Rect inner, double tolerance = 1e-9) =>
        inner.Left >= Left - tolerance &&
        inner.Top >= Top - tolerance &&
        inner.Right <= Right + tolerance &&
        inner.Bottom <= Bottom + tolerance;

    /// <summary>True when the two rectangles overlap by more than <paramref name="tolerance"/>.</summary>
    public bool Intersects(Rect other, double tolerance = 1e-9) =>
        Left < other.Right - tolerance &&
        Right > other.Left + tolerance &&
        Top < other.Bottom - tolerance &&
        Bottom > other.Top + tolerance;
}
