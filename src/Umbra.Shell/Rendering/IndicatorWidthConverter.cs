using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Sizes the running-indicator pill by window state: a long pill for the active window, a short one
/// for a running-but-inactive window.
/// </summary>
/// <remarks>
/// <para>
/// This is how Windows 11 distinguishes active from inactive, and it is the cheap way to do it. The
/// obvious alternative — a lightened background behind the active icon — would add a permanently
/// lit rectangle roughly the size of a whole slot, at a luminance above the icon's own, in a fixed
/// position. Changing the length of a pill that is already lit costs a few pixels either way.
/// </para>
/// <para>
/// The pill is hidden entirely at Tier 0 regardless (§3.1), so none of this costs anything while
/// the pointer is away, which is ~92% of the time.
/// </para>
/// </remarks>
internal sealed class IndicatorWidthConverter : IValueConverter
{
    /// <summary>Pill extent for the foreground window, in device-independent pixels.</summary>
    public double Active { get; set; } = 16.0;

    /// <summary>Pill extent for a running window that is not in front.</summary>
    public double Inactive { get; set; } = 6.0;

    /// <summary>Extent across the pill's short axis, which does not change with state.</summary>
    public double Thickness { get; set; } = 3.0;

    /// <summary>True when this converter is producing the axis the pill grows along.</summary>
    public bool AlongBar { get; set; } = true;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        // A pinned application that is not running has no indicator at all — the icon alone says
        // "this is here", and a pill would say "this is open", which would be a lie.
        if (value is TaskbarSlot { IsRunning: false }) return 0.0;

        if (!AlongBar) return Thickness;

        return value is TaskbarSlot { IsActive: true } ? Active : Inactive;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
