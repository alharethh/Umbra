using Umbra.Shell.Rendering;

namespace Umbra.Shell.Tests;

/// <summary>
/// Closing another application's tray menu when the user clicks the bar.
/// </summary>
/// <remarks>
/// <para>
/// A tray icon's menu belongs to the application that owns the icon, and clicking the bar does
/// not close it: the bar carries <c>WS_EX_NOACTIVATE</c>, so the foreground never moves and the
/// menu's owner is never told it lost anything. Reported as "if i click tray icon and a menu pops
/// up, and i click somewhere empty on the bar it stays open".
/// </para>
/// <para>
/// Only the press edge is unit-tested. The rest — reading menu mode out of
/// <c>GetGUIThreadInfo</c> and posting <c>WM_CANCELMODE</c> to the owner — needs another
/// process's menu on screen and is verified by hand. The edge is where the subtle mistake lives,
/// so it is where the test goes.
/// </para>
/// </remarks>
public class ForeignMenuTests
{
    /// <summary>
    /// <b>The mistake this exists to stop.</b> A held button must fire once, not every tick.
    /// </summary>
    /// <remarks>
    /// The tick runs every 16 ms while the bar is lit. Polling the button <i>state</i> rather
    /// than the edge would cancel the menu in the same gesture that opened it — the click that
    /// opens a tray menu is over the bar, because that is where the icon is, and the button is
    /// still down on the tick after the menu appears. The menu would look like it never opened.
    /// Make <c>PressEdge</c> return <c>buttonDown</c> and this goes red.
    /// </remarks>
    [Fact]
    public void A_Held_Button_Is_An_Edge_Once()
    {
        ForeignMenuDismisser dismisser = new();

        Assert.True(dismisser.PressEdge(true), "the first tick with the button down is the press");
        Assert.False(dismisser.PressEdge(true), "still down is not a new press");
        Assert.False(dismisser.PressEdge(true));
    }

    /// <summary>Releasing and pressing again is a second press.</summary>
    /// <remarks>
    /// The real sequence: press opens the menu, release, then a later press on the bar is the one
    /// that should close it. Latch the edge and never reset it and this goes red.
    /// </remarks>
    [Fact]
    public void Releasing_Then_Pressing_Is_A_New_Edge()
    {
        ForeignMenuDismisser dismisser = new();

        Assert.True(dismisser.PressEdge(true));
        Assert.False(dismisser.PressEdge(false));
        Assert.True(dismisser.PressEdge(true), "a second press after a release is a new press");
    }

    /// <summary>A quiet tick is never a press.</summary>
    [Fact]
    public void No_Button_Is_Never_An_Edge()
    {
        ForeignMenuDismisser dismisser = new();

        Assert.False(dismisser.PressEdge(false));
        Assert.False(dismisser.PressEdge(false));
    }

    /// <summary>
    /// Nothing is open on a test agent, so this must say so rather than say "no menu".
    /// </summary>
    /// <remarks>
    /// A precondition check in the spirit of the rest of this suite: if <c>IsOpen</c> threw or
    /// returned true with no menu on screen, the poll above would be acting on noise.
    /// </remarks>
    [Fact]
    public void With_No_Menu_On_Screen_Nothing_Is_Reported_Open() =>
        Assert.False(ForeignMenuDismisser.IsOpen());
}
