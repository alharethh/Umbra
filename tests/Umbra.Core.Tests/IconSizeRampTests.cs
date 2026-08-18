using Umbra.Core.Layout;
using Umbra.Core.Tiers;

namespace Umbra.Core.Tests;

/// <summary>
/// The icon shrinks while the bar is dormant and grows back on hover. Its permitted drift amplitude
/// moves the opposite way, because §6.2 derives amplitude from the gutter — so the two have to be
/// checked together, at every point on the ramp rather than at its ends.
/// </summary>
public class IconSizeRampTests
{
    private const double Pitch = SlotMetrics.BasePitch;
    private const double Full = SlotMetrics.BaseIconExtent;

    [Fact]
    public void Ramp_Runs_From_Dormant_To_Full()
    {
        Approx.Equal(16.0, IconSizeRamp.Extent(16, 24, 0.0), 1e-9, "dormant end");
        Approx.Equal(20.0, IconSizeRamp.Extent(16, 24, 0.5), 1e-9, "midpoint");
        Approx.Equal(24.0, IconSizeRamp.Extent(16, 24, 1.0), 1e-9, "full end");
    }

    [Fact]
    public void Blend_Is_Clamped_Rather_Than_Extrapolated()
    {
        Approx.Equal(16.0, IconSizeRamp.Extent(16, 24, -3.0), 1e-9, "below the ramp");
        Approx.Equal(24.0, IconSizeRamp.Extent(16, 24, 9.0), 1e-9, "above the ramp");
    }

    [Fact]
    public void A_Dormant_Size_At_Or_Above_Full_Turns_The_Feature_Off()
    {
        // Never ramp downward on hover: that is the opposite of what the feature is for.
        Approx.Equal(24.0, IconSizeRamp.Extent(24, 24, 0.0), 1e-9, "equal sizes, dormant");
        Approx.Equal(24.0, IconSizeRamp.Extent(30, 24, 0.0), 1e-9, "dormant larger, dormant end");
        Approx.Equal(24.0, IconSizeRamp.Extent(30, 24, 1.0), 1e-9, "dormant larger, full end");
    }

    /// <summary>
    /// P5 across the whole ramp, at every tier level, for every dormant size worth shipping.
    /// </summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(18.0)]
    [InlineData(16.0)]
    [InlineData(14.0)]
    [InlineData(12.0)]
    public void Drawn_Icon_Never_Leaves_Its_Slot_At_Any_Point_On_The_Ramp(double dormant)
    {
        for (double level = 0.0; level <= 2.0; level += 0.01)
        {
            double blend = TierPalette.Resolve(BarElement.RunningIcon, level).ChromaBlend;
            double extent = IconSizeRamp.Extent(dormant, Full, blend);

            Assert.True(IconSizeRamp.StaysInSlot(Pitch, extent),
                $"dormant {dormant} at tier {level:F2} drew {extent:F2} px and escaped its slot.");

            // And the amplitude really is the one this extent earns, not the dormant one.
            var slot = new SlotMetrics(Pitch, extent);
            Assert.True(slot.PeakOffset + extent / 2.0 <= Pitch / 2.0,
                $"dormant {dormant} at tier {level:F2}: reach {slot.PeakOffset + extent / 2.0:F3} " +
                $"exceeded the {Pitch / 2.0:F1} px half-slot.");
        }
    }

    /// <summary>
    /// The bug the live path guards against, asserted so the guard cannot be quietly removed.
    /// </summary>
    /// <remarks>
    /// If the drift amplitude is left at the dormant extent's value while the icon is drawn at full
    /// size, the icon escapes its slot. It is silent — it happens only at the turnarounds — and it
    /// does not appear at all until the dormant size drops to 18 px, which is why a test that only
    /// exercised 20 px would report a clean pass on broken code.
    /// </remarks>
    [Theory]
    [InlineData(20.0, false)]   // still fits, by 0.69 px — the guard is not exercised here
    [InlineData(18.0, true)]
    [InlineData(16.0, true)]
    [InlineData(14.0, true)]
    public void Freezing_Amplitude_At_The_Dormant_Size_Breaks_P5(double dormant, bool shouldEscape)
    {
        var dormantSlot = new SlotMetrics(Pitch, dormant);

        // Full-size icon carrying the dormant icon's offset.
        double reach = dormantSlot.PeakOffset + Full / 2.0;
        bool escapes = reach > Pitch / 2.0;

        Assert.True(escapes == shouldEscape,
            $"dormant {dormant}: reach {reach:F3} against {Pitch / 2.0:F1} px, " +
            $"expected {(shouldEscape ? "an escape" : "no escape")}.");
    }

    /// <summary>
    /// §6.0's warning, pinned as a number: the permanently-lit core vanishing buys nothing.
    /// </summary>
    /// <remarks>
    /// Travel is <c>2 × 0.95 × A_max</c> and the core vanishes once it exceeds the icon's own
    /// extent, which solves to <c>e ≤ 0.855p / 1.95</c> — 19.29 px in a 44 px slot. Measured
    /// against the motion model the hottest pixel there is still lit 99.8% of the time. The
    /// milestone is geometric and does not track wear.
    /// </remarks>
    [Fact]
    public void The_Core_Vanishes_Well_Before_The_Wear_Improves()
    {
        double crossover = 0.855 * Pitch / 1.95;
        Approx.Equal(19.2923, crossover, 1e-3, "core-vanishing extent");

        // Straddle it rather than sitting on it: at the crossover travel and extent are equal by
        // construction, so an exact comparison there tests the floating-point unit, not the design.
        double below = crossover - 0.1;
        var justBelow = new SlotMetrics(Pitch, below);
        Assert.True(2 * justBelow.PeakOffset > below,
            "Below the crossover the icon must out-travel its own width, leaving no permanent core.");

        double above = crossover + 0.1;
        var justAbove = new SlotMetrics(Pitch, above);
        Assert.True(2 * justAbove.PeakOffset < above,
            "Above the crossover a permanently-lit core must remain.");
    }
}
