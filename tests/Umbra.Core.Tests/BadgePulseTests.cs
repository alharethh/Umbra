using Umbra.Core.Tiers;

namespace Umbra.Core.Tests;

/// <summary>
/// The badge's drawn pulse (§3.3), integrated rather than read off the constants: the published
/// duty is only true if the waveform that is actually drawn has it.
/// </summary>
public class BadgePulseTests
{
    private static double Integrate(bool decayed, int cycles = 3)
    {
        double cycle = NotificationBadge.CycleSeconds(decayed);
        const double step = 0.0005;
        double lit = 0;

        for (double t = 0; t < cycle * cycles; t += step)
            lit += NotificationBadge.OpacityAt(t + step / 2, decayed) * step;

        return lit / (cycle * cycles);
    }

    [Fact]
    public void Drawn_Duty_Is_A_Quarter()
    {
        Approx.Equal(NotificationBadge.PulseDuty, Integrate(decayed: false), 1e-4, "fresh pulse duty");
    }

    [Fact]
    public void Drawn_Duty_After_Decay_Is_A_Seventh()
    {
        Approx.Equal(NotificationBadge.DecayedPulseDuty, Integrate(decayed: true), 1e-4, "decayed pulse duty");
    }

    [Fact]
    public void The_Lit_Second_Is_Measured_Between_Half_Opacity_Points()
    {
        // 200 ms up, held to one second, 200 ms down: half opacity at 0.1 s and 1.1 s.
        Assert.Equal(0.0, NotificationBadge.OpacityAt(0.0, false));
        Approx.Equal(0.5, NotificationBadge.OpacityAt(0.1, false), 1e-9, "half way up");
        Assert.Equal(1.0, NotificationBadge.OpacityAt(0.5, false));
        Assert.Equal(1.0, NotificationBadge.OpacityAt(0.99, false));
        Approx.Equal(0.5, NotificationBadge.OpacityAt(1.1, false), 1e-9, "half way down");
        Assert.Equal(0.0, NotificationBadge.OpacityAt(1.2, false));
        Assert.Equal(0.0, NotificationBadge.OpacityAt(3.99, false));
        Assert.Equal(0.0, NotificationBadge.OpacityAt(6.5, true));
        Approx.Equal(0.5, NotificationBadge.OpacityAt(7.1, true), 1e-9, "second decayed cycle");
    }
}
