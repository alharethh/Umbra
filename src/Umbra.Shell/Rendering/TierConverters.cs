using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Umbra.Core.Color;
using Umbra.Core.Tiers;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Binds an application icon plus the current tier level to the bitmap that should be drawn.
/// </summary>
internal sealed class TierIconConverter : IMultiValueConverter
{
    private readonly IconPipeline _pipeline = new();

    /// <summary>Which row of the §3.1 table this icon follows.</summary>
    public BarElement Element { get; set; } = BarElement.RunningIcon;

    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return null;
        if (values[0] is not ImageSource source) return null;

        double level = values[1] is double d ? d : 0.0;

        // The application slot, present on the icon row and absent on the tray. Only the row runs
        // the §5.1 cost diagnostic, which needs a title to report against.
        Umbra.Shell.Tasks.TaskbarSlot? slot =
            values.Length > 2 ? values[2] as Umbra.Shell.Tasks.TaskbarSlot : null;

        if (slot is not null) _pipeline.ReportOnce(slot.Title, source);

        return _pipeline.Render(source, Element, level);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The Start glyph's brush: the usual tier grey, or the Windows blue while the Start menu is open.
/// </summary>
/// <remarks>
/// <para>
/// §8 draws the Start glyph neutral because it is "the most temporally invariant element on the
/// entire screen" and four saturated squares is the worst case for something that never moves. That
/// argument is about the resting state. While the menu is actually open the glyph is neither
/// resting nor invariant: the user opened it deliberately, it lasts seconds, and it is the same
/// bargain §3.2 strikes for Tier 2 chroma — full colour at a duty cycle low enough not to matter.
/// </para>
/// <para>
/// Luminance is capped at <see cref="TierPalette.Tier2LuminanceCap"/> for exactly that reason, so
/// the accent is as bright as any other Tier 2 colour and no brighter.
/// </para>
/// </remarks>
internal sealed class StartGlyphBrushConverter : IMultiValueConverter
{
    private readonly TierBrushConverter _resting = new() { Element = BarElement.StartGlyph };

    private SolidColorBrush? _accent;

    /// <summary>The Windows logo blue, before capping.</summary>
    public string Accent { get; set; } = "#4CC2FF";

    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return null;

        // values[1] is StartActive (the menu is open, or the press grace is running); an optional
        // values[2] is pointer-over. Hover colours the glyph for the same reason opening the menu
        // does: it is a deliberate, brief, pointer-driven state, which is exactly the bargain §3.2
        // strikes for Tier 2 chroma.
        bool active = values[1] is true || (values.Length > 2 && values[2] is true);
        if (!active) return _resting.Convert(values[0], targetType, parameter, culture);

        return _accent ??= Build(Srgb.CapLuminance(Rgb.FromHex(Accent), TierPalette.Tier2LuminanceCap));
    }

    private static SolidColorBrush Build(Rgb colour)
    {
        SolidColorBrush brush = new(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Binds the current tier level to the brush for a flat element — the running indicator, the clock,
/// the Start glyph.
/// </summary>
/// <remarks>
/// Interpolation happens in <see cref="TierPalette.Resolve"/>, which works in linear light. Doing
/// it on the encoded values instead makes the ramp spend more time bright than intended (§3.4), and
/// it is the kind of mistake that looks perfectly fine on screen.
/// </remarks>
internal sealed class TierBrushConverter : IValueConverter
{
    private readonly Dictionary<int, SolidColorBrush> _cache = [];

    /// <summary>Which row of the §3.1 table this element follows.</summary>
    public BarElement Element { get; set; } = BarElement.RunningIndicator;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        double level = value is double d ? d : 0.0;

        // Quantise so a smooth ramp does not allocate a brush per frame.
        int quantised = (int)Math.Round(Math.Clamp(level, 0.0, 2.0) * 128);
        if (_cache.TryGetValue(quantised, out SolidColorBrush? cached)) return cached;

        Rgb colour = TierPalette.Resolve(Element, quantised / 128.0).Monochrome;
        SolidColorBrush brush = new(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();

        _cache[quantised] = brush;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
