using System.Windows;
using System.Windows.Media.Animation;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Motion;
using Umbra.Shell.Rendering;
using Umbra.Shell.Shell;

namespace Umbra.Shell;

/// <summary>
/// The bar going black while nobody is there. See <see cref="AwayBlackout"/> for the rule.
/// </summary>
/// <remarks>
/// <para>
/// Only the content fades. The window, its black background and its AppBar reservation stay
/// exactly as they are, so nothing reflows (§6.3) and the first input finds the bar where it was.
/// Once the fade completes the content is hidden as well, which stops the clock repainting a
/// strip nobody can see (§6.6).
/// </para>
/// <para>
/// Polled from the proximity tick, which is already running at 100 ms at rest: the bar comes back
/// within one tick of any input, and nothing new wakes the machine.
/// </para>
/// </remarks>
internal partial class BarWindow
{
    /// <summary>How long the fade to black takes. Coming back is immediate.</summary>
    internal static readonly TimeSpan AwayFade = TimeSpan.FromSeconds(1.5);

    private readonly AwayBlackout _away = new(AudioPlayback.AnyStreamActive);

    /// <summary>True once the fade has finished: the strip emits no light and records no wear.</summary>
    private bool _blackedOut;

    /// <summary>Takes the delay from the settings. Called at startup and on every live change.</summary>
    public void ApplyAwaySetting(Settings.UmbraSettings settings)
    {
        _away.DelaySeconds = settings.AwayAfterMinutes * 60.0;
        PollAway();
    }

    private void PollAway()
    {
        if (!_away.Update(MotionDriver.SecondsSinceLastInput(), Environment.TickCount64 / 1000.0)) return;

        if (_away.IsAway) FadeToBlack();
        else ComeBack();
    }

    private void FadeToBlack()
    {
        BringUpLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"away: fading to black after {_away.DelaySeconds:F0} s without input"));

        CloseEverythingLit();

        DoubleAnimation fade = new(0.0, AwayFade) { EasingFunction = new LinearLightFade() };
        fade.Completed += (_, _) =>
        {
            // A fade that finished after the user came back must not hide a bar that is in use.
            if (!_away.IsAway) return;
            _blackedOut = true;
            ContentRoot.Visibility = Visibility.Hidden;
        };

        ContentRoot.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Closes every surface the bar has open beyond its own content: the panels, the volume level
    /// and any stock tooltip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each of these is a window of its own, so fading <c>ContentRoot</c> does not reach them, and
    /// measured on master the "Show desktop" tooltip (peak 247/255), the overflow flyout and the
    /// context menu all stayed lit over the black strip for as long as nobody came back. A pointer
    /// left resting on the bar is the ordinary way to walk away, so a tooltip is the common case.
    /// </para>
    /// <para>
    /// <b>Closed rather than kept lit, and closed rather than holding the blackout off.</b> Holding
    /// it off while a panel is open would leave a menu, the flyout and the bar under it at full
    /// tier for as long as the user is gone, which is the case the feature exists for. Nothing is
    /// lost by closing: a panel left untouched for the whole away delay is not in use, and none of
    /// these holds anything the user typed. Closed at the start of the fade, not its end, so they
    /// are not drawn at full brightness over a darkening bar for a second and a half. Windows' own
    /// surfaces (Start, Quick Settings) are not ours and are left alone, as in
    /// <see cref="ClearPanelsFor"/>.
    /// </para>
    /// </remarks>
    private void CloseEverythingLit()
    {
        ClearPanelsFor(BarPanel.None);
        CloseVolumeTip();
        int tips = OpenToolTips.CloseAll();
        if (tips > 0) BringUpLog.Write($"away: closed {tips} tooltip(s)");
    }

    private void ComeBack()
    {
        // Removing the animation and setting the value, not animating back: the first input is
        // the moment the user looks, and a fade-in would be the bar arriving late.
        ContentRoot.BeginAnimation(OpacityProperty, null);
        ContentRoot.Opacity = 1.0;
        ContentRoot.Visibility = Visibility.Visible;
        _blackedOut = false;

        BringUpLog.Write("away: back");
    }

    /// <summary>
    /// Eases a 1 → 0 opacity animation so the light, not the sRGB value, falls linearly.
    /// </summary>
    private sealed class LinearLightFade : IEasingFunction
    {
        public double Ease(double normalizedTime) =>
            1.0 - AwayBlackout.OpacityForLight(1.0 - normalizedTime);
    }
}
