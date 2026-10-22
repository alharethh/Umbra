using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Umbra.Core.Layout;
using Umbra.Shell.Motion;

namespace Umbra.Shell.Tests;

/// <summary>
/// The clock on a left or right bar, and whether its drift across the bar carries its digits off it.
/// </summary>
/// <remarks>
/// On a vertical bar the short axis runs across the clock's text. From Tier 1 the text is
/// <c>HH:mm:ss</c> over a numeric date, both about 51 DIP in a 56 DIP bar, and the clock's
/// short-axis excursion is several DIP. Seen on the 150% laptop as the seconds' and the year's last
/// digits cut off by the bar's edge, constant for minutes at a time.
/// </remarks>
public class VerticalClockTests
{
    private const double TimeFontSize = 14.0;
    private const double DateFontSize = 10.0;

    private static BarMetrics Bar(BarEdge edge) =>
        new(edge, Alignment.Start, scale: 1.0, baseIconExtent: 16.0);

    /// <summary>The clock's own short-axis excursion: everything else's, halved.</summary>
    private static double ClockPeakAcross(BarMetrics metrics) =>
        metrics.ShortAxisPeakOffset * MotionDriver.ClockShortAxisScale;

    private static double Width(string text, double size) =>
        new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            Brushes.White,
            1.5).WidthIncludingTrailingWhitespace;

    /// <summary>The engaged clock's widest line, as en-US draws it.</summary>
    private static double EngagedWidth() =>
        Math.Max(Width("00:00:00", TimeFontSize), Width("10/22/2026", DateFontSize));

    /// <summary>
    /// The bug, as the number that causes it: unclamped, the engaged clock's edge passes the
    /// bar's edge at a drift extreme.
    /// </summary>
    [Theory]
    [InlineData(BarEdge.Left)]
    [InlineData(BarEdge.Right)]
    public void Unclamped_Drift_Would_Carry_The_Engaged_Clock_Off_The_Bar(BarEdge edge)
    {
        BarMetrics metrics = Bar(edge);

        Assert.True(
            EngagedWidth() / 2.0 + ClockPeakAcross(metrics) > metrics.ContentBand / 2.0,
            $"the premise no longer holds: {EngagedWidth():F2} DIP of text with ±" +
            $"{ClockPeakAcross(metrics):F2} DIP of drift fits a {metrics.ContentBand} DIP bar");
    }

    /// <summary>The fix: across a vertical bar, the clock is held inside the bar's thickness.</summary>
    [Theory]
    [InlineData(BarEdge.Left)]
    [InlineData(BarEdge.Right)]
    public void The_Engaged_Clock_Never_Leaves_The_Bar(BarEdge edge)
    {
        BarMetrics metrics = Bar(edge);
        double thickness = metrics.ContentBand;
        double width = EngagedWidth();
        double peak = ClockPeakAcross(metrics);

        // The whole excursion, both ways: a sign error shows on one side only.
        for (double offset = -peak; offset <= peak; offset += 0.05)
        {
            double held = BarWindow.ClampClockAcross(offset, thickness, width);
            Assert.InRange(held - width / 2.0, -thickness / 2.0, thickness / 2.0);
            Assert.InRange(held + width / 2.0, -thickness / 2.0, thickness / 2.0);
        }
    }

    /// <summary>
    /// The dormant clock keeps all of its drift: the clamp exists for the wide engaged text and
    /// must not cost the wear protection of the state the clock spends most of the day in.
    /// </summary>
    [Theory]
    [InlineData(BarEdge.Left)]
    [InlineData(BarEdge.Right)]
    public void The_Dormant_Clock_Keeps_Its_Whole_Drift(BarEdge edge)
    {
        BarMetrics metrics = Bar(edge);
        double width = Width("00:00", TimeFontSize);
        double peak = ClockPeakAcross(metrics);

        Assert.Equal(peak, BarWindow.ClampClockAcross(peak, metrics.ContentBand, width), 9);
        Assert.Equal(-peak, BarWindow.ClampClockAcross(-peak, metrics.ContentBand, width), 9);
    }

    /// <summary>Text wider than the bar is held centred rather than thrown to one side.</summary>
    [Fact]
    public void A_Clock_Wider_Than_The_Bar_Is_Held_Centred()
    {
        Assert.Equal(0.0, BarWindow.ClampClockAcross(4.0, 56.0, 60.0));
        Assert.Equal(0.0, BarWindow.ClampClockAcross(-4.0, 56.0, 60.0));
    }
}
