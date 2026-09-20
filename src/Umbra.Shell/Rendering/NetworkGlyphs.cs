using System.Windows.Media;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Rendering;

/// <summary>
/// The three network glyphs, drawn rather than sourced from an icon.
/// </summary>
/// <remarks>
/// <para>
/// Stroke-only paths on a nominal 12 x 11 canvas, to match the overflow chevron: a stroked path
/// takes the tier brush like every other drawn element, so the indicator darkens with the rest of
/// the bar. A bitmap icon would have to go through the monochrome normaliser instead, and would be
/// the only element on the bar whose luminance is not ours to choose.
/// </para>
/// <para>
/// Frozen, because these are built once and assigned on a property that the UI reads on every
/// render — an unfrozen <see cref="Geometry"/> carries change tracking for a shape that will never
/// change.
/// </para>
/// </remarks>
internal static class NetworkGlyphs
{
    /// <summary>Wi-Fi: two arcs over a dot.</summary>
    /// <remarks>
    /// The dot is a line of no useful length with round caps, which renders as a disc of the
    /// stroke's width. A zero-length line is not guaranteed to render at all, hence the 0.2.
    /// </remarks>
    private static readonly Geometry WiFi = Frozen(
        "M 0.8,4.6 A 7.4,7.4 0 0 1 11.2,4.6 " +
        "M 3.1,7.0 A 4.2,4.2 0 0 1 8.9,7.0 " +
        "M 5.9,9.6 L 6.1,9.6");

    /// <summary>Ethernet: a screen on a stand, as Windows draws its wired connection.</summary>
    private static readonly Geometry Ethernet = Frozen(
        "M 1.5,2.2 H 10.5 V 7.8 H 1.5 Z " +
        "M 6,7.8 V 9.6 " +
        "M 3.6,9.6 H 8.4");

    /// <summary>Offline: a globe with a slash through it.</summary>
    private static readonly Geometry Offline = Frozen(
        "M 1.2,5.9 A 4.7,4.7 0 1 0 10.6,5.9 A 4.7,4.7 0 1 0 1.2,5.9 " +
        "M 1.2,5.9 H 10.6 " +
        "M 5.9,1.2 A 4.2,4.7 0 0 1 5.9,10.6 A 4.2,4.7 0 0 1 5.9,1.2 " +
        "M 2.2,9.6 L 9.6,2.2");

    /// <summary>The glyph for a connection kind.</summary>
    public static Geometry For(NetworkKind kind) => kind switch
    {
        NetworkKind.WiFi => WiFi,
        NetworkKind.Ethernet => Ethernet,
        _ => Offline,
    };

    private static Geometry Frozen(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();

        return geometry;
    }
}
