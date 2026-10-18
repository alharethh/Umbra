using Umbra.Core.Layout;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// Re-registering the AppBar after Explorer restarts, and never after the bar's own broadcasts.
/// </summary>
/// <remarks>
/// Measured on the 2256×1504 laptop at 150%: the bar is (0,1432)–(2256,1504), the work area with the
/// reservation held is (0,0)–(2256,1432), and after a shell Explorer restart it was the full
/// (0,0)–(2256,1504) at +5, +15 and +30 s.
/// </remarks>
public class ExplorerRestartWatchTests
{
    private const uint OldExplorer = 4100;
    private const uint NewExplorer = 9200;

    private static readonly Rect Bar = new(0, 1432, 2256, 72);
    private static readonly Rect Held = new(0, 0, 2256, 1432);
    private static readonly Rect FullScreen = new(0, 0, 2256, 1504);

    /// <summary>
    /// The bar's own <c>TaskbarCreated</c> broadcasts — startup, +5 s, the menu — come from the same
    /// shell and must not arm anything. Re-registering there reflows every maximised window.
    /// </summary>
    [Fact]
    public void The_Bars_Own_Broadcast_Arms_Nothing()
    {
        ExplorerRestartWatch watch = new(OldExplorer);

        Assert.False(watch.OnTaskbarCreated(OldExplorer));
        Assert.False(watch.Armed);
        Assert.Equal(ReservationVerdict.Idle, watch.Check(OldExplorer, reservationHeld: false));
    }

    /// <summary>The QA repro: Explorer restarted, the reservation is gone, so re-register.</summary>
    [Fact]
    public void A_Restarted_Explorer_With_The_Reservation_Gone_Re_Registers()
    {
        ExplorerRestartWatch watch = new(OldExplorer);

        Assert.True(watch.OnTaskbarCreated(NewExplorer));
        Assert.Equal(ReservationVerdict.Reregister,
            watch.Check(NewExplorer, ExplorerRestartWatch.ReservationHeld(FullScreen, Bar, BarEdge.Bottom)));

        // The registration took: the watch ends, and the bar now belongs to the new shell, so a
        // later broadcast of the bar's own is ignored again.
        Assert.Equal(ReservationVerdict.Settled,
            watch.Check(NewExplorer, ExplorerRestartWatch.ReservationHeld(Held, Bar, BarEdge.Bottom)));
        Assert.False(watch.Armed);
        Assert.False(watch.OnTaskbarCreated(NewExplorer));
    }

    /// <summary>
    /// A starting Explorer reserves the bottom for its own taskbar until the watchdog autohides it,
    /// and with both bars the same height that reads as held. The watch must not settle on that.
    /// </summary>
    [Fact]
    public void A_Reservation_Lost_Late_Is_Still_Caught()
    {
        ExplorerRestartWatch watch = new(OldExplorer);
        watch.OnTaskbarCreated(NewExplorer);

        for (int i = 0; i < 5; i++)
            Assert.Equal(ReservationVerdict.KeepWatching, watch.Check(NewExplorer, reservationHeld: true));

        Assert.Equal(ReservationVerdict.Reregister, watch.Check(NewExplorer, reservationHeld: false));
    }

    /// <summary>A restart that left the reservation in place is never re-registered.</summary>
    [Fact]
    public void A_Restart_That_Kept_The_Reservation_Does_Nothing_And_Ends()
    {
        ExplorerRestartWatch watch = new(OldExplorer);
        watch.OnTaskbarCreated(NewExplorer);

        for (int i = 1; i < ExplorerRestartWatch.MaxChecks; i++)
            Assert.Equal(ReservationVerdict.KeepWatching, watch.Check(NewExplorer, reservationHeld: true));

        Assert.Equal(ReservationVerdict.Settled, watch.Check(NewExplorer, reservationHeld: true));
        Assert.False(watch.Armed);
        Assert.Equal(NewExplorer, watch.KnownShell);
    }

    /// <summary>
    /// If re-registering never brings the reservation back, stop. A loop here would reflow the
    /// desktop once a second.
    /// </summary>
    [Fact]
    public void Re_Registering_That_Does_Not_Help_Gives_Up()
    {
        ExplorerRestartWatch watch = new(OldExplorer);
        watch.OnTaskbarCreated(NewExplorer);

        for (int i = 0; i < ExplorerRestartWatch.MaxReregistrations; i++)
            Assert.Equal(ReservationVerdict.Reregister, watch.Check(NewExplorer, reservationHeld: false));

        Assert.Equal(ReservationVerdict.GaveUp, watch.Check(NewExplorer, reservationHeld: false));
        Assert.False(watch.Armed);
    }

    /// <summary>
    /// No shell window at the moment of the message arms the watch; the reservation check is still
    /// what decides.
    /// </summary>
    [Fact]
    public void An_Unknown_Shell_Arms_But_A_Held_Reservation_Is_Left_Alone()
    {
        ExplorerRestartWatch watch = new(OldExplorer);

        Assert.True(watch.OnTaskbarCreated(0));
        Assert.Equal(ReservationVerdict.KeepWatching, watch.Check(0, reservationHeld: true));
    }

    /// <summary>A second message while a watch is running does not restart its count.</summary>
    [Fact]
    public void A_Second_Message_While_Watching_Does_Not_Re_Arm()
    {
        ExplorerRestartWatch watch = new(OldExplorer);
        watch.OnTaskbarCreated(NewExplorer);

        Assert.False(watch.OnTaskbarCreated(NewExplorer));
        Assert.True(watch.Armed);
    }

    /// <summary>Every edge, held and missing, including another AppBar stacked further in.</summary>
    [Theory]
    [InlineData(BarEdge.Bottom, 0, 1432, 2256, 72, 0, 0, 2256, 1432, true)]
    [InlineData(BarEdge.Bottom, 0, 1432, 2256, 72, 0, 0, 2256, 1400, true)]
    [InlineData(BarEdge.Bottom, 0, 1432, 2256, 72, 0, 0, 2256, 1504, false)]
    [InlineData(BarEdge.Top, 0, 0, 2256, 72, 0, 72, 2256, 1432, true)]
    [InlineData(BarEdge.Top, 0, 0, 2256, 72, 0, 0, 2256, 1504, false)]
    [InlineData(BarEdge.Left, 0, 0, 84, 1504, 84, 0, 2172, 1504, true)]
    [InlineData(BarEdge.Left, 0, 0, 84, 1504, 0, 0, 2256, 1504, false)]
    [InlineData(BarEdge.Right, 2172, 0, 84, 1504, 0, 0, 2172, 1504, true)]
    [InlineData(BarEdge.Right, 2172, 0, 84, 1504, 0, 0, 2256, 1504, false)]
    public void The_Work_Area_Says_Whether_The_Strip_Is_Reserved(
        BarEdge edge,
        double bx, double by, double bw, double bh,
        double wx, double wy, double ww, double wh,
        bool held) =>
        Assert.Equal(held, ExplorerRestartWatch.ReservationHeld(new Rect(wx, wy, ww, wh), new Rect(bx, by, bw, bh), edge));
}
