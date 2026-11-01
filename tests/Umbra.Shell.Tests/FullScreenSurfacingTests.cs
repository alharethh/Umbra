using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// The bar over a fullscreen application: Win shows it and Win again hides it, unless the bar was
/// pressed, in which case only a press elsewhere hides it.
/// </summary>
public class FullScreenSurfacingTests
{
    private readonly FullScreenSurfacing _surfacing = new();

    private SurfacingChange Tick(bool startOpen, bool pressedElsewhere = false) =>
        _surfacing.Tick(startOpen, pressedElsewhere);

    [Fact]
    public void Win_Raises_The_Bar_And_Win_Again_Lowers_It()
    {
        Assert.Equal(SurfacingChange.Raise, Tick(startOpen: true));
        Assert.Equal(SurfacingChange.None, Tick(startOpen: true));
        Assert.Equal(SurfacingChange.Lower, Tick(startOpen: false));
    }

    // The report: a right-click on an icon closes Start so the menu is not drawn under it, and
    // following Start alone took the bar and the menu away on the next tick.
    [Fact]
    public void A_Press_On_The_Bar_Keeps_It_Up_After_Start_Closes()
    {
        Tick(startOpen: true);
        _surfacing.BarPressed();

        Assert.Equal(SurfacingChange.None, Tick(startOpen: false));
        Assert.Equal(SurfacingChange.None, Tick(startOpen: false));
        Assert.True(_surfacing.Raised);
    }

    [Fact]
    public void A_Held_Bar_Goes_On_The_First_Press_Elsewhere()
    {
        Tick(startOpen: true);
        _surfacing.BarPressed();
        Tick(startOpen: false);

        Assert.Equal(SurfacingChange.Lower, Tick(startOpen: false, pressedElsewhere: true));
        Assert.False(_surfacing.Raised);
    }

    // Owner's call: once the bar has been used, Win opening and closing Start again leaves it up.
    [Fact]
    public void Win_Twice_Does_Not_Lower_A_Held_Bar()
    {
        Tick(startOpen: true);
        _surfacing.BarPressed();
        Tick(startOpen: false);

        Assert.Equal(SurfacingChange.None, Tick(startOpen: true));
        Assert.Equal(SurfacingChange.None, Tick(startOpen: false));
        Assert.True(_surfacing.Raised);
    }

    [Fact]
    public void A_Press_Elsewhere_While_Start_Is_Open_Lowers_The_Bar_When_Start_Closes()
    {
        Tick(startOpen: true);
        _surfacing.BarPressed();

        Assert.Equal(SurfacingChange.None, Tick(startOpen: true, pressedElsewhere: true));
        Assert.Equal(SurfacingChange.Lower, Tick(startOpen: false));
    }

    [Fact]
    public void A_Press_While_The_Bar_Has_Given_Way_Holds_Nothing()
    {
        _surfacing.BarPressed();

        Assert.Equal(SurfacingChange.Raise, Tick(startOpen: true));
        Assert.Equal(SurfacingChange.Lower, Tick(startOpen: false));
    }

    [Fact]
    public void A_Hold_Does_Not_Outlive_The_Bar_Going_Down()
    {
        Tick(startOpen: true);
        _surfacing.BarPressed();
        Tick(startOpen: false, pressedElsewhere: true);

        Tick(startOpen: true);
        Assert.Equal(SurfacingChange.Lower, Tick(startOpen: false));
    }

    [Fact]
    public void Reset_Drops_The_Hold()
    {
        Tick(startOpen: true);
        _surfacing.BarPressed();
        _surfacing.Reset();

        Assert.False(_surfacing.Raised);
        Assert.Equal(SurfacingChange.Raise, Tick(startOpen: true));
        Assert.Equal(SurfacingChange.Lower, Tick(startOpen: false));
    }
}
