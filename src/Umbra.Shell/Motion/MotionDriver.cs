using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Umbra.Core.Layout;
using Umbra.Core.Motion;

namespace Umbra.Shell.Motion;

/// <summary>
/// Drives the drift and micro-jitter offsets applied to the bar's content (design-spec §6).
/// </summary>
/// <remarks>
/// <para>
/// Ticks at 2 Hz, which is the micro-jitter update rate (§6.1) and far more than enough for drift —
/// at 0.018 px/s the drift moves 0.009 px between ticks. v1 implied continuous animation, which
/// would have been 5.2 million repaints a day to accomplish nothing measurable between frames.
/// </para>
/// <para>
/// Motion stops on session lock, suspend, user idle beyond five minutes, and when the system's
/// animation setting is off (§6.6). That is not only a power optimisation: WOLED panels run their
/// own compensation cycles when idle, and a process that repaints continuously can prevent the
/// display from ever getting there — a motion layer that suppressed the panel's own, far more
/// effective, mitigation would be a net loss. Motion resumes from its stored phase rather than
/// from zero, because restarting at zero would snap the content back to the extreme of its travel.
/// </para>
/// </remarks>
internal sealed class MotionDriver : IDisposable
{
    private readonly DispatcherTimer _timer;
    /// <summary>
    /// Start a quarter of a period in, so the triangle begins at the centre of its travel rather
    /// than at an extreme.
    /// </summary>
    /// <remarks>
    /// Wear-neutral — a triangle's residence distribution does not depend on where it starts — but
    /// starting at phase zero puts the row 5.46 px off its undrifted position the instant the bar
    /// appears, and it would take ten minutes to come back. Starting centred means the bar always
    /// launches looking like the layout says it should.
    /// </remarks>
    private readonly MotionClock _clock = new(MotionModel.DriftPeriodSeconds * 0.25);
    private readonly BarMetrics _metrics;
    private DateTime _lastTick = DateTime.UtcNow;
    private int _loggedTicks;

    public MotionDriver(BarMetrics metrics)
    {
        _metrics = metrics;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(1.0 / MotionModel.MicroUpdateRateHz),
        };
        _timer.Tick += OnTick;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (!SystemParameters.ClientAreaAnimation)
            _clock.Suppress(MotionSuppression.AnimationsDisabled);
    }

    /// <summary>Raised when the offset changes, in device-independent pixels.</summary>
    public event Action<double, double>? OffsetChanged;

    /// <summary>Whether motion is currently running, and why not if it is not.</summary>
    public MotionSuppression Suppression => _clock.Suppression;

    public void Start() => _timer.Start();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = Math.Clamp((now - _lastTick).TotalSeconds, 0.0, 1.0);
        _lastTick = now;

        UpdateIdleSuppression();

        if (!_clock.IsRunning) return;

        _clock.Advance(elapsed);

        MotionOffset offset = MotionModel.IconRow(
            _clock.PhaseSeconds, _metrics.Slot, _metrics.ShortAxisDriftAmplitude);

        (double dx, double dy) = offset.ToScreen(_metrics.Edge);
        OffsetChanged?.Invoke(dx, dy);

        // The first few ticks only. Motion is imperceptible by design (P6), so a log is the only
        // practical way to confirm it is running and that the offsets are fractional rather than
        // snapped — and then it should go quiet rather than write to disk forever.
        if (_loggedTicks < 12)
        {
            _loggedTicks++;
            Diagnostics.BringUpLog.Write(System.String.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"motion tick {_loggedTicks}: phase={_clock.PhaseSeconds:F2}s " +
                $"along={offset.Along:F4} across={offset.Across:F4} dx={dx:F4} dy={dy:F4}"));
        }
    }

    private void UpdateIdleSuppression()
    {
        double idle = SecondsSinceLastInput();

        if (idle >= MotionClock.IdleThresholdSeconds) _clock.Suppress(MotionSuppression.UserIdle);
        else _clock.Release(MotionSuppression.UserIdle);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
            _clock.Suppress(MotionSuppression.SessionLocked);
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            _clock.Release(MotionSuppression.SessionLocked);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _clock.Suppress(MotionSuppression.DisplayOff);
        else if (e.Mode == PowerModes.Resume) _clock.Release(MotionSuppression.DisplayOff);
    }

    private static double SecondsSinceLastInput()
    {
        NativeMethods.LASTINPUTINFO info = new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
        };

        if (!NativeMethods.GetLastInputInfo(ref info)) return 0.0;

        // Both are 32-bit millisecond tick counts and both wrap after ~49 days; the subtraction is
        // correct across the wrap as long as it stays in unsigned arithmetic.
        uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return elapsed / 1000.0;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    }
}
