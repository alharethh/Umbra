using System.Windows;
using System.Windows.Controls.Primitives;
using Umbra.Core.Layout;
using Umbra.Shell.Rendering;

namespace Umbra.Shell.Tests;

/// <summary>
/// Where the notification-area overflow flyout opens.
/// </summary>
/// <remarks>
/// <para>
/// Reported as "needs to be centered on the arrow button, not to the right of my screen". The
/// cause is that <c>Placement="Top"</c> aligns the popup's <i>left</i> edge with the chevron's
/// left edge, and the chevron sits near the trailing end of the bar — so a five-column panel ran
/// past the end of the screen and Windows pushed it back inboard, which is what made it read as
/// right-aligned rather than as an alignment bug.
/// </para>
/// <para>
/// The numbers below are this machine's: a 2560 px screen, a 32 px chevron slot, and a flyout
/// about 180 px across.
/// </para>
/// </remarks>
public class FlyoutPlacementTests
{
    private static readonly Size Chevron = new(32, 32);
    private static readonly Size Panel = new(180, 96);

    private const double Gap = FlyoutPlacement.Gap;

    /// <summary>The flyout's centre on the bar's long axis, relative to the chevron's.</summary>
    private static double LongAxisCentreError(BarEdge edge)
    {
        Point placed = FlyoutPlacement.Centred(edge, Panel, Chevron);

        return edge.IsVertical()
            ? placed.Y + (Panel.Height / 2) - (Chevron.Height / 2)
            : placed.X + (Panel.Width / 2) - (Chevron.Width / 2);
    }

    /// <summary>The fix, on every edge: the panel and the chevron share a centre line.</summary>
    [Theory]
    [InlineData(BarEdge.Bottom)]
    [InlineData(BarEdge.Top)]
    [InlineData(BarEdge.Left)]
    [InlineData(BarEdge.Right)]
    public void The_Flyout_Is_Centred_On_The_Chevron(BarEdge edge) =>
        Assert.Equal(0.0, LongAxisCentreError(edge), 6);

    /// <summary>
    /// And on the short axis it clears the bar by the gap, on the inboard side of the edge.
    /// </summary>
    /// <remarks>
    /// Signs matter more than magnitudes here: getting one wrong puts the flyout underneath the
    /// bar, off the screen entirely, where it is invisible rather than merely misplaced.
    /// </remarks>
    [Fact]
    public void The_Flyout_Sits_Inboard_Of_The_Bar()
    {
        // Bottom bar: the panel's bottom edge is Gap above the chevron's top.
        Assert.Equal(-(Panel.Height + Gap), FlyoutPlacement.Centred(BarEdge.Bottom, Panel, Chevron).Y, 6);

        // Top bar: its top edge is Gap below the chevron's bottom.
        Assert.Equal(Chevron.Height + Gap, FlyoutPlacement.Centred(BarEdge.Top, Panel, Chevron).Y, 6);

        // Left bar: to the right of the chevron.
        Assert.Equal(Chevron.Width + Gap, FlyoutPlacement.Centred(BarEdge.Left, Panel, Chevron).X, 6);

        // Right bar: to the left of it.
        Assert.Equal(-(Panel.Width + Gap), FlyoutPlacement.Centred(BarEdge.Right, Panel, Chevron).X, 6);
    }

    /// <summary>
    /// The bug, stated as the number that caused it.
    /// </summary>
    /// <remarks>
    /// A chevron 128 px from the end of a 2560 px screen, which is roughly where it lands with a
    /// few tray icons and the clock outboard of it. Left-aligned — what <c>PlacementMode.Top</c>
    /// does — the panel needs 180 px of the 128 that are left and is shoved back inboard by
    /// Windows. Centred, it needs 90 and fits.
    /// </remarks>
    [Fact]
    public void Left_Aligned_The_Panel_Runs_Off_The_Screen_And_Centred_It_Does_Not()
    {
        const double screenWidth = 2560;
        const double chevronLeft = screenWidth - 128;

        double leftAligned = chevronLeft + Panel.Width;
        Assert.True(
            leftAligned > screenWidth,
            $"the premise no longer holds: a left-aligned panel ends at {leftAligned:F0} px, " +
            $"inside the {screenWidth:F0} px screen, so nothing would have pushed it inboard");

        double centred = chevronLeft + FlyoutPlacement.Centred(BarEdge.Bottom, Panel, Chevron).X + Panel.Width;
        Assert.True(
            centred <= screenWidth,
            $"centred, the panel still ends at {centred:F0} px on a {screenWidth:F0} px screen");
    }

    /// <summary>
    /// When it does not fit, WPF slides it <b>along</b> the bar and never off it.
    /// </summary>
    /// <remarks>
    /// The short axis is the one that must not move. Nudging the flyout that way lifts it off the
    /// bar, opening a gap the pointer has to cross — and the watcher that closes the flyout counts
    /// a pointer over neither the flyout nor the chevron as having left.
    /// </remarks>
    [Theory]
    [InlineData(BarEdge.Bottom, PopupPrimaryAxis.Horizontal)]
    [InlineData(BarEdge.Top, PopupPrimaryAxis.Horizontal)]
    [InlineData(BarEdge.Left, PopupPrimaryAxis.Vertical)]
    [InlineData(BarEdge.Right, PopupPrimaryAxis.Vertical)]
    public void It_Slides_Along_The_Bar(BarEdge edge, PopupPrimaryAxis expected) =>
        Assert.Equal(expected, FlyoutPlacement.SlideAxis(edge));

    /// <summary>
    /// A panel narrower than the chevron is centred too, rather than pinned to one side.
    /// </summary>
    /// <remarks>
    /// Reachable with a single icon behind the chevron, and the sign of the offset flips there —
    /// which is exactly the kind of case a formula written for the common direction gets wrong.
    /// </remarks>
    [Fact]
    public void A_Panel_Narrower_Than_The_Chevron_Is_Still_Centred()
    {
        Size narrow = new(20, 40);
        Point placed = FlyoutPlacement.Centred(BarEdge.Bottom, narrow, Chevron);

        Assert.Equal(6.0, placed.X, 6);
        Assert.Equal(-(narrow.Height + Gap), placed.Y, 6);
    }
}
