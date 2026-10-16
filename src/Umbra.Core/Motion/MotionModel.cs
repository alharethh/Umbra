using Umbra.Core.Layout;

namespace Umbra.Core.Motion;

/// <summary>A displacement along and across the bar's long axis, in device-independent pixels.</summary>
public readonly record struct MotionOffset(double Along, double Across)
{
    /// <summary>Maps the offset onto screen axes for a given edge.</summary>
    public (double Dx, double Dy) ToScreen(BarEdge edge) =>
        edge.IsVertical() ? (Across, Along) : (Along, Across);

    /// <summary>Adds two offsets.</summary>
    public static MotionOffset operator +(MotionOffset a, MotionOffset b) =>
        new(a.Along + b.Along, a.Across + b.Across);
}

/// <summary>
/// The motion layer of design-spec §6: slot-bounded drift plus micro-jitter, applied as a single
/// render transform on the content group.
/// </summary>
/// <remarks>
/// <para>
/// <b>Constant velocity, no easing.</b> v1 specified "triangular with eased turnarounds"; easing
/// dwells longer at the extremes, concentrating residence time at exactly the two positions the
/// drift is trying to avoid, producing two hot bands. A uniform wear distribution requires a
/// constant-velocity triangle. There is nothing to smooth anyway — the turnaround happens at
/// 0.018 px/s.
/// </para>
/// <para>
/// <b>Decorrelate by frequency, not phase.</b> v1 gave the clock a phase offset on the icon row's
/// frequency. Wear integrates over many cycles, so a phase offset produces an identical
/// accumulated distribution, merely time-shifted — it decorrelates the instantaneous appearance
/// and nothing else. The clock therefore runs different periods (§6.4).
/// </para>
/// <para>
/// Whether any of this earns its complexity is spec §11.2 Q1, and is why the wear accumulator
/// ships before the motion layer.
/// </para>
/// </remarks>
public static class MotionModel
{
    /// <summary>Drift period, 20 minutes (§6.2).</summary>
    /// <remarks>
    /// A triangle's residence distribution is period-independent, so this costs nothing in wear
    /// terms. It was shortened from v1's 45 min so that a session shorter than the period does not
    /// sample a biased portion of the cycle and leave the bar parked off-centre.
    /// </remarks>
    public const double DriftPeriodSeconds = 20.0 * 60.0;

    /// <summary>
    /// Period of the short-axis drift, incommensurable with <see cref="DriftPeriodSeconds"/>.
    /// </summary>
    /// <remarks>
    /// §6.2 fixes the short-axis amplitude but not its period; this 0.61 ratio is taken from the
    /// prototype. The prototype drives the short axis with a sine, which has an arcsine residence
    /// distribution — densest at the turnarounds, the very failure §6.2 removed easing to avoid.
    /// It is a triangle here for that reason.
    /// </remarks>
    public const double ShortAxisDriftPeriodSeconds = 0.61 * DriftPeriodSeconds;

    /// <summary>
    /// Motion time at which the bar launches: a quarter period in, where the long-axis triangle
    /// crosses its centre.
    /// </summary>
    public const double LaunchPhaseSeconds = 0.25 * DriftPeriodSeconds;

    /// <summary>
    /// Shift applied to the short-axis triangle so it, too, crosses its centre at
    /// <see cref="LaunchPhaseSeconds"/>.
    /// </summary>
    /// <remarks>
    /// Without it the shorter period puts the short axis 41% of the way through its cycle at
    /// launch, which is +0.64 of its amplitude: every launch started the row and the clock low on a
    /// bottom bar, and on a fixed line of text against the screen edge that reads as the clock
    /// being off centre. Wear-neutral for the same reason the launch phase is: a triangle's
    /// residence distribution does not depend on where it starts. The micro-jitter sinusoids are
    /// measured from the launch phase for the same reason, so they start at zero too.
    /// </remarks>
    public const double ShortAxisPhaseSeconds = 0.25 * ShortAxisDriftPeriodSeconds - LaunchPhaseSeconds;

    /// <summary>Micro-jitter update rate, 2 Hz (§6.1).</summary>
    /// <remarks>
    /// v1 implied continuous animation. At ±2 px over a 60 s period a 60 Hz repaint moves the
    /// content 0.0035 px per frame — 5.2 million repaints a day to accomplish nothing measurable
    /// between frames. 2 Hz steps 0.105 px, which is ample for varying the antialiasing gradient,
    /// at thirty times less GPU wake-up.
    /// </remarks>
    public const double MicroUpdateRateHz = 2.0;

    /// <summary>First micro-jitter period of the icon row, 60 s (§6.1).</summary>
    public const double IconMicroPeriodASeconds = 60.0;

    /// <summary>Second micro-jitter period of the icon row, 83 s — incommensurable with the first.</summary>
    public const double IconMicroPeriodBSeconds = 83.0;

    /// <summary>First micro-jitter period of the clock, 71 s (§6.4).</summary>
    public const double ClockMicroPeriodASeconds = 71.0;

    /// <summary>Second micro-jitter period of the clock, 97 s.</summary>
    public const double ClockMicroPeriodBSeconds = 97.0;

    /// <summary>
    /// A constant-velocity triangle wave in −1..1, starting at −1 and rising. Slope magnitude is
    /// <c>4 / period</c> everywhere except the two instantaneous turnarounds.
    /// </summary>
    public static double Triangle(double seconds, double period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);

        double phase = seconds / period;
        phase -= Math.Floor(phase);                        // 0..1, correct for negative time
        return phase < 0.5 ? phase * 4.0 - 1.0 : 3.0 - phase * 4.0;
    }

    /// <summary>Slot-bounded drift displacement at a given motion time.</summary>
    public static double Drift(double seconds, double amplitude, double period = DriftPeriodSeconds) =>
        amplitude * Triangle(seconds, period);

    /// <summary>
    /// Drift speed, <c>4A / P</c> — constant apart from the turnarounds. At ±5.46 px over 20
    /// minutes this is 0.0182 px/s, or 66 px/hour.
    /// </summary>
    public static double DriftVelocity(double amplitude, double period = DriftPeriodSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        return 4.0 * amplitude / period;
    }

    /// <summary>
    /// One micro-jitter axis: a sinusoid sampled at <paramref name="rateHz"/>.
    /// </summary>
    /// <remarks>
    /// The result is deliberately fractional. Integer-snapped jitter quantises into a handful of
    /// discrete states that each still accumulate; sub-pixel offsets mean the edge gradient itself
    /// lands differently every step. WPF's <c>TextFormattingMode="Display"</c> snaps glyphs to the
    /// pixel grid and silently defeats this — see §5.2.
    /// </remarks>
    public static double Micro(double seconds, double amplitude, double period,
                               double rateHz = MicroUpdateRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rateHz);

        double quantised = Math.Floor(seconds * rateHz) / rateHz;
        return amplitude * Math.Sin(2.0 * Math.PI * quantised / period);
    }

    /// <summary>
    /// Largest displacement between two consecutive micro-jitter updates,
    /// <c>A · 2π / (period · rate)</c>. 0.102 px for the icon row; 0.0035 px if it were driven at
    /// 60 Hz instead.
    /// </summary>
    public static double MicroStep(double amplitude, double period,
                                   double rateHz = MicroUpdateRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rateHz);
        return amplitude * 2.0 * Math.PI / (period * rateHz);
    }

    /// <summary>
    /// Total offset of the icon row's content group — drift plus micro-jitter, coherent across the
    /// whole group so icons never reorder or move relative to each other.
    /// </summary>
    public static MotionOffset IconRow(double seconds, SlotMetrics slot, double shortAxisAmplitude)
    {
        double along = Drift(seconds, slot.DriftAmplitude)
                     + Micro(seconds - LaunchPhaseSeconds, slot.MicroAmplitude, IconMicroPeriodASeconds);
        double across = Drift(seconds + ShortAxisPhaseSeconds, shortAxisAmplitude, ShortAxisDriftPeriodSeconds)
                      + Micro(seconds - LaunchPhaseSeconds, slot.MicroAmplitude, IconMicroPeriodBSeconds);
        return new MotionOffset(along, across);
    }

    /// <summary>
    /// Total offset of the clock. Drifts with the content group, but jitters on its own
    /// frequencies so its wear pattern decorrelates from the icon row's (§6.4).
    /// </summary>
    public static MotionOffset Clock(double seconds, SlotMetrics slot, double shortAxisAmplitude)
    {
        double along = Drift(seconds, slot.DriftAmplitude)
                     + Micro(seconds - LaunchPhaseSeconds, slot.MicroAmplitude, ClockMicroPeriodASeconds);
        double across = Drift(seconds + ShortAxisPhaseSeconds, shortAxisAmplitude, ShortAxisDriftPeriodSeconds)
                      + Micro(seconds - LaunchPhaseSeconds, slot.MicroAmplitude, ClockMicroPeriodBSeconds);
        return new MotionOffset(along, across);
    }
}
