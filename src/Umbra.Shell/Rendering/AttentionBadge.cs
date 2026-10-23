using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Umbra.Core.Color;
using Umbra.Core.Tiers;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Runs the §3.3 pulse on an element's opacity while its slot is asking for attention:
/// <c>AttentionBadge.State="{Binding Attention}"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An animation, not a tick.</b> The motion driver ticks at 2 Hz at rest, too coarse for
/// §3.3's 200 ms fades, and a timer of our own would be a standing wake-up for a badge that is
/// almost never up. A WPF animation exists only while it is applied: <see cref="AttentionState.None"/>
/// removes it, so a bar with nothing asking renders nothing for it.
/// </para>
/// <para>
/// The key frames come from <see cref="NotificationBadge.OpacityAt"/>'s own breakpoints, so the
/// lit duty the animation draws is the one the Core tests integrate: 0.25, and 1/7 once decayed.
/// </para>
/// </remarks>
internal static class AttentionBadge
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(AttentionState), typeof(AttentionBadge),
        new PropertyMetadata(AttentionState.None, OnStateChanged));

    public static AttentionState GetState(DependencyObject element) => (AttentionState)element.GetValue(StateProperty);

    public static void SetState(DependencyObject element, AttentionState value) => element.SetValue(StateProperty, value);

    private static readonly DoubleAnimationUsingKeyFrames Fresh = Build(decayed: false);
    private static readonly DoubleAnimationUsingKeyFrames Decayed = Build(decayed: true);

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        AttentionState state = (AttentionState)e.NewValue;

        // Removing the animation drops the opacity back to its local value, 0: not drawn.
        element.BeginAnimation(UIElement.OpacityProperty, PulseFor(state));
        element.Visibility = state == AttentionState.None ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The animation for a state, or null for none.</summary>
    internal static DoubleAnimationUsingKeyFrames? PulseFor(AttentionState state) => state switch
    {
        AttentionState.Pulsing => Fresh,
        AttentionState.Decayed => Decayed,
        _ => null,
    };

    /// <summary>
    /// One cycle as linear key frames: dark, up over the fade, held, down over the fade, dark.
    /// Starts lit-rising, so a new request is seen at once rather than up to three seconds later.
    /// </summary>
    internal static DoubleAnimationUsingKeyFrames Build(bool decayed)
    {
        double fade = NotificationBadge.FadeSeconds;
        double on = NotificationBadge.PulseOnSeconds;
        double cycle = NotificationBadge.CycleSeconds(decayed);

        DoubleAnimationUsingKeyFrames animation = new()
        {
            Duration = TimeSpan.FromSeconds(cycle),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        foreach (double t in new[] { 0.0, fade, on, on + fade, cycle })
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(
                NotificationBadge.OpacityAt(Math.Min(t, cycle - 1e-9), decayed),
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));

        animation.Freeze();
        return animation;
    }
}

/// <summary>
/// The badge's colour: <c>#B3B3B3</c>, or <c>#808080</c> once ignored, at Tiers 0 and 1; the
/// capped accent at Tier 2 (§3.1). Values: tier level, <see cref="AttentionState"/>, accent colour.
/// </summary>
internal sealed class AttentionBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        double level = values.Length > 0 && values[0] is double d ? d : 0.0;
        AttentionState state = values.Length > 1 && values[1] is AttentionState s ? s : AttentionState.None;
        Color? accent = values.Length > 2 && values[2] is Color c ? c : null;

        SolidColorBrush brush = new(ColourFor(level, state, accent));
        brush.Freeze();
        return brush;
    }

    /// <summary>The rule, without a brush.</summary>
    internal static Color ColourFor(double level, AttentionState state, Color? accent)
    {
        // Tier 2 only once fully engaged: the badge does not ramp through the proximity band, it
        // is already lit from Tier 0 and has nothing to ramp towards but the accent.
        if (level >= 2.0 && accent is { } a) return a;

        Rgb grey = state == AttentionState.Decayed ? NotificationBadge.DecayedColour : NotificationBadge.Colour;
        return Color.FromRgb(grey.R, grey.G, grey.B);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
