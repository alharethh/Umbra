#if DEBUG
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Umbra.Core.Layout;
using Umbra.Core.Motion;

namespace Umbra.Shell.Motion;

/// <summary>
/// A slider that winds motion time forward, so the drift can be seen rather than only logged.
/// </summary>
/// <remarks>
/// <para>
/// P6 says motion the user can perceive has failed, and §6.2 delivers that: 0.018 px/s, a full
/// traverse taking ten minutes. The consequence is that the motion layer is invisible at 1×, and
/// "invisible" and "not running" look identical. This is the instrument that tells them apart.
/// </para>
/// <para>
/// The slider is logarithmic. A linear one spends nearly all its travel above 500×, where every
/// setting looks the same, and gives no useful resolution over the 1–50× range where the drift
/// first becomes noticeable — which is the range the perceptibility question actually lives in.
/// </para>
/// <para>
/// <b>Debug builds only.</b> This file is compiled out by <c>#if DEBUG</c>, and the XAML half is
/// removed from every other configuration in <c>Umbra.Shell.csproj</c>.
/// </para>
/// </remarks>
internal partial class MotionSpeedWindow : Window
{
    private readonly MotionDriver _motion;
    private readonly BarMetrics _metrics;
    private readonly Action<bool> _setWearSuspended;

    public MotionSpeedWindow(MotionDriver motion, BarMetrics metrics, Action<bool> setWearSuspended)
    {
        _motion = motion;
        _metrics = metrics;
        _setWearSuspended = setWearSuspended;

        InitializeComponent();
        Update(_motion.TimeScale);
    }

    /// <summary>Slider position 0–1 to a multiplier, logarithmically.</summary>
    private static double ToScale(double position) =>
        Math.Pow(MotionDriver.MaxTimeScale, position);

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        Update(ToScale(e.NewValue));
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        Speed.Value = 0;
        Update(1.0);
    }

    private void Update(double scale)
    {
        _motion.TimeScale = scale;
        scale = _motion.TimeScale;

        bool simulating = scale > 1.0;

        // Wear data is only worth having if it describes what the panel actually saw. Simulated
        // motion did not happen, so it is not credited.
        _setWearSuspended(simulating);

        // A triangle at constant velocity covers four amplitudes per period (§6.2).
        double velocity = 4.0 * _metrics.Slot.DriftAmplitude / MotionModel.DriftPeriodSeconds * scale;
        double periodSeconds = MotionModel.DriftPeriodSeconds / scale;

        Readout.Text = string.Create(CultureInfo.InvariantCulture, $"{scale,8:F0}x");

        Detail.Text = string.Create(CultureInfo.InvariantCulture,
            $"drift {velocity:F3} px/s   full traverse {periodSeconds / 60.0:F2} min   " +
            $"amplitude +/-{_metrics.Slot.DriftAmplitude:F2} px (unchanged)");

        WearNote.Text = simulating
            ? "Wear recording paused while simulating."
            : "Wear recording live.";
    }

    protected override void OnClosed(EventArgs e)
    {
        // Never leave the bar running fast, or recording nothing, because a window was closed.
        _motion.TimeScale = 1.0;
        _setWearSuspended(false);

        base.OnClosed(e);
    }
}
#endif
