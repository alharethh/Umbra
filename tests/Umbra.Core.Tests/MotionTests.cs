using Umbra.Core.Layout;
using Umbra.Core.Motion;

namespace Umbra.Core.Tests;

/// <summary>Design-spec §6 — the motion model, and the v1 corrections it encodes.</summary>
public class MotionTests
{
    private static readonly SlotMetrics Slot = SlotMetrics.ForScale(1.0);

    [Fact]
    public void Drift_Velocity_Is_The_Published_Figure()
    {
        Approx.Equal(0.0182, MotionModel.DriftVelocity(5.46), 1e-4, "drift velocity (±5.46, 20 min)");
        Approx.Equal(66.0, MotionModel.DriftVelocity(5.46) * 3600.0, 1.0, "px per hour");
    }

    [Fact]
    public void Micro_Step_At_2Hz_Is_The_Published_Figure()
    {
        Approx.Equal(0.102, MotionModel.MicroStep(1.95, 60.0), 1e-3, "micro step @2 Hz (A = 1.95, T = 60 s)");
    }

    [Fact]
    public void Sixty_Hertz_Would_Move_The_Content_By_Nothing()
    {
        // §6.1's correction. At ±2 px over a 60 s period a 60 Hz repaint moves the content
        // 0.0035 px per frame — 5.2 million repaints a day to accomplish nothing measurable
        // between frames. 2 Hz steps 0.105 px for identical benefit at thirty times less wake-up.
        double at60Hz = MotionModel.MicroStep(2.0, 60.0, rateHz: 60.0);
        double at2Hz = MotionModel.MicroStep(2.0, 60.0, rateHz: 2.0);

        Approx.Equal(0.0035, at60Hz, 1e-4, "step per frame at 60 Hz");
        Approx.Equal(0.105, at2Hz, 1e-3, "step per update at 2 Hz");
        Approx.Relative(30.0, at2Hz / at60Hz, 1e-9, "the ratio the update rate buys");
    }

    [Fact]
    public void Drift_Is_A_Constant_Velocity_Triangle()
    {
        // Sampled speed must be flat everywhere except the two turnarounds.
        const double dt = 1.0;
        List<double> speeds = [];

        for (double t = 0.0; t < MotionModel.DriftPeriodSeconds; t += dt)
        {
            double a = MotionModel.Drift(t, Slot.DriftAmplitude);
            double b = MotionModel.Drift(t + dt, Slot.DriftAmplitude);
            speeds.Add(Math.Abs(b - a) / dt);
        }

        // Two samples straddle a turnaround; every other sample runs at the constant velocity.
        List<double> steady = [.. speeds.Order().Take(speeds.Count - 2)];

        Approx.Equal(0.0182, steady[^1], 1e-4, "fastest steady sample");
        Approx.Equal(0.0182, steady[0], 1e-4, "slowest steady sample");
    }

    [Fact]
    public void Constant_Velocity_Spreads_Residence_Evenly_And_Easing_Does_Not()
    {
        // §6.2's first correction, and the one that would fail silently: eased turnarounds dwell
        // longer at the extremes, concentrating residence time at exactly the two positions drift
        // exists to avoid, and burning two hot bands instead of one.
        // A sinusoid stands in for the eased turnaround — it is the limiting case of easing, and
        // its residence density goes as 1/√(1−x²), piling up against both extremes.
        double[] triangle = Residence(t => MotionModel.Triangle(t, MotionModel.DriftPeriodSeconds));
        double[] eased = Residence(t => Math.Sin(2.0 * Math.PI * t / MotionModel.DriftPeriodSeconds));

        double triangleSpread = Spread(triangle);
        double easedSpread = Spread(eased);

        Assert.True(triangleSpread < 0.05,
            $"A constant-velocity triangle must distribute wear evenly (spread {triangleSpread:P1}).");
        Assert.True(easedSpread > 0.5,
            $"An eased turnaround must show the hot bands §6.2 removed it for (spread {easedSpread:P1}).");

        // And the shape of the failure: the extremes, not the centre, are where it piles up.
        Assert.True(eased[0] > eased[eased.Length / 2] * 1.5, "Eased motion favours the extremes.");
    }

    [Fact]
    public void Phase_Offset_Does_Not_Decorrelate_Accumulated_Wear()
    {
        // §6.4's correction. v1 gave the clock a phase offset on the icon row's frequency. Wear
        // integrates over many cycles, so the accumulated spatial distribution comes out
        // identical — the offset decorrelates the instantaneous appearance and nothing else.
        const double period = 60.0;
        const double phaseOffset = 0.4 * period;

        double[] unshifted = Residence(t => Math.Sin(2.0 * Math.PI * t / period), span: 6000.0);
        double[] shifted = Residence(t => Math.Sin(2.0 * Math.PI * (t + phaseOffset) / period), span: 6000.0);

        for (int i = 0; i < unshifted.Length; i++)
            Approx.Relative(unshifted[i], shifted[i], 0.02,
                $"residence in bin {i} is unchanged by a phase offset");

        // The instantaneous positions do differ — which is exactly the thing that does not matter.
        double correlation = Correlation(
            t => Math.Sin(2.0 * Math.PI * t / period),
            t => Math.Sin(2.0 * Math.PI * (t + phaseOffset) / period));

        Assert.True(Math.Abs(correlation) > 0.5,
            $"Same frequency stays strongly correlated whatever the phase (ρ = {correlation:F3}).");
    }

    [Fact]
    public void Different_Frequencies_Do_Decorrelate()
    {
        double correlation = Correlation(
            t => Math.Sin(2.0 * Math.PI * t / MotionModel.IconMicroPeriodASeconds),
            t => Math.Sin(2.0 * Math.PI * t / MotionModel.ClockMicroPeriodASeconds));

        Assert.True(Math.Abs(correlation) < 0.05,
            $"60 s against 71 s must decorrelate over a long window (ρ = {correlation:F3}).");
    }

    [Fact]
    public void Clock_Runs_Different_Frequencies_From_The_Icon_Row()
    {
        // §6.4: the clock's digits change but their positions do not, and the colon never changes
        // at all. It needs its own frequencies, not its own phase.
        Assert.NotEqual(MotionModel.IconMicroPeriodASeconds, MotionModel.ClockMicroPeriodASeconds);
        Assert.NotEqual(MotionModel.IconMicroPeriodBSeconds, MotionModel.ClockMicroPeriodBSeconds);

        double correlation = Correlation(
            t => MotionModel.Clock(t, Slot, 4.8).Along - MotionModel.Drift(t, Slot.DriftAmplitude),
            t => MotionModel.IconRow(t, Slot, 4.8).Along - MotionModel.Drift(t, Slot.DriftAmplitude));

        Assert.True(Math.Abs(correlation) < 0.1,
            $"Clock jitter must not track icon-row jitter (ρ = {correlation:F3}).");
    }

    [Fact]
    public void Micro_Jitter_Is_Fractional_And_Updates_At_2Hz()
    {
        // §6.1: fractional positioning matters more than amplitude. Integer-snapped jitter
        // quantises into a handful of discrete states that each still accumulate; sub-pixel
        // offsets mean the edge gradient lands differently every step. WPF's
        // TextFormattingMode="Display" silently defeats this (§5.2).
        HashSet<double> updates = [];
        for (int i = 0; i < 1000; i++)
            updates.Add(MotionModel.Micro(i * 0.01, Slot.MicroAmplitude, MotionModel.IconMicroPeriodASeconds));

        Assert.Equal(20, updates.Count);                                     // 2 Hz over 10 s
        Assert.True(updates.Count(v => v != Math.Round(v)) > 15,
            "Offsets must be fractional, not snapped to whole pixels.");
    }

    [Fact]
    public void Total_Offset_Stays_Within_The_Slot_Over_A_Full_Cycle()
    {
        double peak = 0.0;
        for (double t = 0.0; t < MotionModel.DriftPeriodSeconds; t += 0.5)
            peak = Math.Max(peak, Math.Abs(MotionModel.IconRow(t, Slot, 4.8).Along));

        Assert.True(peak <= Slot.PeakOffset + 1e-9, $"Peak along-axis travel {peak:F3} exceeded {Slot.PeakOffset:F3}.");
        Assert.True(peak < Slot.Gutter, "P5: drawn content never leaves its slot.");
    }

    [Fact]
    public void Triangle_Handles_Negative_Time()
    {
        Approx.Equal(MotionModel.Triangle(100.0, 60.0), MotionModel.Triangle(-20.0, 60.0), 1e-12,
            "phase must wrap correctly below zero");
    }

    [Fact]
    public void Motion_Resumes_From_Its_Stored_Phase()
    {
        // §6.6: motion pauses on lock, idle and display power-down so the panel's own compensation
        // cycles can run — and resumes from its stored phase, not from zero. Restarting at zero
        // would snap the content group back to the extreme of its travel.
        MotionClock clock = new();
        clock.Advance(500.0);

        clock.Suppress(MotionSuppression.SessionLocked);
        clock.Advance(10_000.0);

        Assert.False(clock.IsRunning);
        Assert.Equal(500.0, clock.PhaseSeconds);

        clock.Release(MotionSuppression.SessionLocked);
        clock.Advance(100.0);

        Assert.True(clock.IsRunning);
        Assert.Equal(600.0, clock.PhaseSeconds);
    }

    [Fact]
    public void Every_Suppression_Reason_Must_Clear_Before_Motion_Resumes()
    {
        MotionClock clock = new();
        clock.Suppress(MotionSuppression.UserIdle);
        clock.Suppress(MotionSuppression.DisplayOff);

        clock.Release(MotionSuppression.UserIdle);
        clock.Advance(50.0);

        Assert.False(clock.IsRunning);
        Assert.Equal(0.0, clock.PhaseSeconds);

        clock.Release(MotionSuppression.DisplayOff);
        clock.Advance(50.0);

        Assert.Equal(50.0, clock.PhaseSeconds);
    }

    [Fact]
    public void Idle_Threshold_Is_Five_Minutes()
    {
        Assert.Equal(300.0, MotionClock.IdleThresholdSeconds);
    }

    [Fact]
    public void Drift_Period_Is_Twenty_Minutes_So_Short_Sessions_See_The_Whole_Range()
    {
        // §6.2: a triangle's residence distribution is period-independent, so shortening the
        // period from v1's 45 min costs nothing in wear terms. It matters because a 30-minute
        // session against a 45-minute period leaves the bar parked off-centre for most of it.
        Assert.Equal(1200.0, MotionModel.DriftPeriodSeconds);

        double min = double.MaxValue, max = double.MinValue;
        for (double t = 0.0; t < 1800.0; t += 1.0)          // a 30-minute session
        {
            double x = MotionModel.Drift(t, Slot.DriftAmplitude);
            min = Math.Min(min, x);
            max = Math.Max(max, x);
        }

        Approx.Equal(-Slot.DriftAmplitude, min, 0.05, "a short session reaches the negative extreme");
        Approx.Equal(Slot.DriftAmplitude, max, 0.05, "a short session reaches the positive extreme");
    }

    /// <summary>Residence histogram: how long the waveform spends in each of 20 bins over −1..1.</summary>
    private static double[] Residence(Func<double, double> wave, double span = 12_000.0, int bins = 20)
    {
        double[] histogram = new double[bins];
        const double dt = 0.05;

        for (double t = 0.0; t < span; t += dt)
        {
            double value = Math.Clamp(wave(t), -1.0, 1.0);
            int bin = Math.Min(bins - 1, (int)((value + 1.0) / 2.0 * bins));
            histogram[bin] += dt;
        }

        return histogram;
    }

    /// <summary>Spread of a histogram: peak-to-trough as a fraction of the mean.</summary>
    private static double Spread(double[] histogram) =>
        (histogram.Max() - histogram.Min()) / histogram.Average();

    /// <summary>Pearson correlation of two waveforms sampled over a long window.</summary>
    private static double Correlation(Func<double, double> a, Func<double, double> b,
                                      double span = 12_000.0, double dt = 0.5)
    {
        List<double> xs = [], ys = [];
        for (double t = 0.0; t < span; t += dt)
        {
            xs.Add(a(t));
            ys.Add(b(t));
        }

        double meanX = xs.Average(), meanY = ys.Average();
        double covariance = 0.0, varianceX = 0.0, varianceY = 0.0;

        for (int i = 0; i < xs.Count; i++)
        {
            double dx = xs[i] - meanX, dy = ys[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        return covariance / Math.Sqrt(varianceX * varianceY);
    }
}
