using Umbra.Core.Layout;

namespace Umbra.Shell.Tests;

/// <summary>
/// The running-indicator pill, and whether short-axis drift carries it off the bar.
/// </summary>
/// <remarks>
/// <para>
/// Every other drifting element is centred in the content band, so the band's own clearance absorbs
/// the short-axis travel. The pill is the exception: it is pinned to the <i>outer</i> edge of the
/// bar with a 1 px margin, because that is where Windows 11 puts it. Its clearance on that side is
/// therefore 1 px and nothing else.
/// </para>
/// <para>
/// That did not matter while the reserved rectangle was 8 px deeper than the content band. It does
/// now: <c>BaseHorizontalReserved</c> was cut from 56 to 48 to match the Windows taskbar exactly,
/// and the reasoning recorded for that change considered the icon — centred, with 12 px of
/// clearance — and not the one element that has none.
/// </para>
/// </remarks>
public class RunningIndicatorTests
{
    /// <summary>Clearance between the pill and the screen edge, from <c>BarWindow.PillMargin</c>.</summary>
    private const double PillMargin = BarWindow.PillEdgeClearance;

    private static BarMetrics Bar(double dormantIcon) =>
        new(BarEdge.Bottom, Alignment.Start, scale: 1.0, baseIconExtent: dormantIcon);

    /// <summary>Peak short-axis excursion: §6.2 drift plus the micro-jitter on the same axis.</summary>
    private static double PeakShortAxisOffset(BarMetrics metrics) => metrics.ShortAxisPeakOffset;

    /// <summary>
    /// The bug, stated as the number that causes it.
    /// </summary>
    /// <remarks>
    /// At the shipping dormant size of 16 px the pill is carried several times its own 1 px of
    /// clearance past the edge of the window, where it is clipped — partly for most of the drift
    /// cycle and entirely at the extremes. From the user's side: the line under a running
    /// application is sometimes drawn wrong and sometimes not drawn at all, on any edge, with no
    /// pattern, because the drift period is minutes long.
    /// </remarks>
    [Theory]
    [InlineData(24.0)]
    [InlineData(16.0)]
    [InlineData(12.0)]
    public void UnclampedShortAxisDriftWouldCarryThePillOffTheBar(double dormant)
    {
        BarMetrics metrics = Bar(dormant);

        Assert.True(
            PeakShortAxisOffset(metrics) > PillMargin,
            $"the premise of this test no longer holds at {dormant} px: peak short-axis offset " +
            $"{PeakShortAxisOffset(metrics):F2} px is within the pill's {PillMargin} px clearance, " +
            "so clamping it would be unnecessary");
    }

    /// <summary>
    /// The fix: the pill's short-axis offset is clamped to the clearance it actually has.
    /// </summary>
    /// <remarks>
    /// P5 applied per element rather than per slot. Clamping rather than zeroing keeps whatever
    /// short-axis travel the geometry genuinely allows, instead of answering a containment problem by
    /// switching the motion off.
    /// </remarks>
    [Theory]
    [InlineData(24.0)]
    [InlineData(20.0)]
    [InlineData(16.0)]
    [InlineData(12.0)]
    public void ClampedPillNeverLeavesTheBar(double dormant)
    {
        BarMetrics metrics = Bar(dormant);
        double peak = PeakShortAxisOffset(metrics);

        // Sweep the whole excursion, both directions, not just the extremes: the clamp has to hold
        // everywhere, and a sign error shows up only on one side.
        for (double offset = -peak; offset <= peak; offset += 0.05)
        {
            double clamped = BarWindow.ClampPillOffset(offset, PillMargin);

            Assert.True(
                Math.Abs(clamped) <= PillMargin + 1e-9,
                $"pill offset {clamped:F3} exceeds its {PillMargin} px clearance at {dormant} px");
        }
    }

    /// <summary>Inside the clearance, the pill drifts exactly as everything else does.</summary>
    [Fact]
    public void SmallOffsetsPassThroughUntouched()
    {
        Assert.Equal(0.4, BarWindow.ClampPillOffset(0.4, PillMargin), 9);
        Assert.Equal(-0.4, BarWindow.ClampPillOffset(-0.4, PillMargin), 9);
        Assert.Equal(0.0, BarWindow.ClampPillOffset(0.0, PillMargin), 9);
    }

    /// <summary>The long axis is untouched: there is nothing to clip into along the bar.</summary>
    /// <remarks>
    /// Worth pinning, because the cheap fix for this bug is to stop the pill drifting at all, and
    /// that would throw away the long-axis travel too — which is the half that actually spreads the
    /// pill's wear, since the pill is 16 px long and only 3 px thick.
    /// </remarks>
    [Fact]
    public void TheClampIsShortAxisOnly()
    {
        BarMetrics metrics = Bar(16.0);
        double along = metrics.Slot.PeakOffset;

        Assert.True(along > PillMargin, "premise: long-axis travel exceeds the short-axis clearance");
        Assert.Equal(along, BarWindow.ClampPillOffset(along, double.PositiveInfinity), 9);
    }
}
