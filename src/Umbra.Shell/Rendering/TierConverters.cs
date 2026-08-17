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
        bool outline = values.Length > 2 && values[2] is true;

        if (values.Length > 3 && values[3] is string title)
            _pipeline.ReportOnce(title, source);

        return _pipeline.Render(source, Element, level, outline);
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
