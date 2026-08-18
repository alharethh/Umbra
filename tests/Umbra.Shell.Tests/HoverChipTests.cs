using Umbra.Core.Layout;
using Umbra.Core.Tiers;
using Umbra.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// The hover chip drawn behind an icon the pointer is over.
/// </summary>
/// <remarks>
/// The chip is concentric with the icon and drifts with it, so it is wider than the thing P5
/// already guarantees fits. Its margin therefore has to come out of the 5%-of-pitch safety margin
/// §6.2 holds back inside the gutter, and nothing larger is legal. Getting this wrong puts the chip
/// over the neighbouring slot only at the drift turnarounds — rarely, briefly, and never while
/// anyone is looking for it.
/// </remarks>
public class HoverChipTests
{
    private const double Pitch = SlotMetrics.BasePitch;
    // The engaged size, which is the tightest case: the largest icon has the least gutter to
    // drift in, so it is where the chip's margin has the least room.
    private const double Full = BarWindow.ActiveIconExtent;

    private static double ChipReach(double iconExtent)
    {
        var slot = new SlotMetrics(Pitch, iconExtent);
        return iconExtent / 2.0 + BarWindow.HoverChipMargin + slot.PeakOffset;
    }

    /// <summary>P5, for every icon size and every point on the dormant → full-size ramp.</summary>
    [Theory]
    [InlineData(24.0)]
    [InlineData(20.0)]
    [InlineData(18.0)]
    [InlineData(16.0)]
    [InlineData(14.0)]
    [InlineData(12.0)]
    public void HoverChipStaysInItsSlot(double dormant)
    {
        for (double level = 0.0; level <= 2.0; level += 0.01)
        {
            double blend = TierPalette.Resolve(BarElement.RunningIcon, level).ChromaBlend;
            double extent = IconSizeRamp.Extent(dormant, Full, blend);

            Assert.True(ChipReach(extent) <= Pitch / 2.0,
                $"dormant {dormant} at tier {level:F2}: chip reached {ChipReach(extent):F3} " +
                $"against a {Pitch / 2.0:F1} px half-slot.");
        }
    }

    /// <summary>
    /// The margin is the largest one that works, so a wider chip must fail. Without this the test
    /// above would pass just as happily on a margin of zero, and would not be pinning anything.
    /// </summary>
    [Fact]
    public void A_Wider_Chip_Would_Break_P5()
    {
        // The tightest case is the largest icon, which has the least gutter to drift in.
        var slot = new SlotMetrics(Pitch, Full);
        double headroom = Pitch / 2.0 - (Full / 2.0 + slot.PeakOffset);

        Assert.True(BarWindow.HoverChipMargin <= headroom,
            $"margin {BarWindow.HoverChipMargin} exceeds the {headroom:F3} px available.");

        // And it is genuinely close to the limit rather than trivially small.
        Assert.True(BarWindow.HoverChipMargin > headroom / 2.0,
            $"margin {BarWindow.HoverChipMargin} is far below the {headroom:F3} px available; " +
            "this test is not constraining anything.");

        double tooWide = headroom + 0.5;
        Assert.False(Full / 2.0 + tooWide + slot.PeakOffset <= Pitch / 2.0,
            "A margin beyond the headroom must not fit, or the arithmetic here is wrong.");
    }
}
