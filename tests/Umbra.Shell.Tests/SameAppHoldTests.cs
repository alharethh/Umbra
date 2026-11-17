using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// A bar that gave way to a fullscreen application stays down while that application's own
/// smaller window is in front (VLC's controls), and comes up once it is not.
/// </summary>
public class SameAppHoldTests
{
    private const uint Vlc = 7656;
    private const uint Other = 4242;

    private readonly SameAppHold _hold = new();

    private static SameAppFacts Facts(uint conceded = Vlc, uint foreground = Vlc, bool still = true) =>
        new(conceded, foreground, still);

    // The report: clicking play/pause in VLC's fullscreen controls brought the bar up over them.
    [Fact]
    public void The_Apps_Own_Window_In_Front_Keeps_The_Bar_Down()
    {
        Assert.True(_hold.OnLeave(Facts()));
        Assert.True(_hold.Holding);
    }

    [Fact]
    public void Another_App_In_Front_Brings_The_Bar_Up()
    {
        Assert.False(_hold.OnLeave(Facts(foreground: Other)));
        Assert.False(_hold.Holding);
    }

    // Leaving fullscreen with the same app in front: its window is no longer fullscreen.
    [Fact]
    public void An_App_No_Longer_Fullscreen_Brings_The_Bar_Up()
    {
        Assert.False(_hold.OnLeave(Facts(still: false)));
    }

    // A closed window and an empty foreground both read as process 0, which is not one app.
    [Fact]
    public void A_Gone_Window_With_Nothing_In_Front_Brings_The_Bar_Up()
    {
        Assert.False(_hold.OnLeave(Facts(conceded: 0, foreground: 0, still: true)));
    }

    [Fact]
    public void A_Held_Bar_Stays_Down_While_Nothing_Changes()
    {
        _hold.OnLeave(Facts());

        Assert.Equal(SameAppChange.Keep, _hold.Tick(listedAgain: false, Facts()));
        Assert.Equal(SameAppChange.Keep, _hold.Tick(listedAgain: false, Facts()));
        Assert.True(_hold.Holding);
    }

    // VLC hands the foreground back to its video window and ManagedShell lists it again.
    [Fact]
    public void A_Held_Bar_Rejoins_When_ManagedShell_Lists_The_App_Again()
    {
        _hold.OnLeave(Facts());

        Assert.Equal(SameAppChange.Rejoin, _hold.Tick(listedAgain: true, Facts()));
        Assert.False(_hold.Holding);
    }

    [Fact]
    public void A_Held_Bar_Comes_Up_When_Another_App_Takes_The_Front()
    {
        _hold.OnLeave(Facts());

        Assert.Equal(SameAppChange.Rise, _hold.Tick(listedAgain: false, Facts(foreground: Other)));
        Assert.False(_hold.Holding);
    }

    [Fact]
    public void A_Held_Bar_Comes_Up_When_The_App_Leaves_Fullscreen()
    {
        _hold.OnLeave(Facts());

        Assert.Equal(SameAppChange.Rise, _hold.Tick(listedAgain: false, Facts(still: false)));
    }

    [Fact]
    public void A_Held_Bar_Comes_Up_When_The_App_Closes()
    {
        _hold.OnLeave(Facts());

        Assert.Equal(SameAppChange.Rise, _hold.Tick(listedAgain: false, Facts(conceded: 0, foreground: 0)));
    }

    [Fact]
    public void A_Bar_Not_Holding_Is_Left_Alone()
    {
        Assert.Equal(SameAppChange.Keep, _hold.Tick(listedAgain: false, Facts(foreground: Other)));
        Assert.Equal(SameAppChange.Keep, _hold.Tick(listedAgain: true, Facts()));
    }

    [Fact]
    public void Reset_Ends_The_Hold()
    {
        _hold.OnLeave(Facts());
        _hold.Reset();

        Assert.False(_hold.Holding);
        Assert.Equal(SameAppChange.Keep, _hold.Tick(listedAgain: false, Facts(foreground: Other)));
    }
}
