using Umbra.Shell.Rendering;

namespace Umbra.Shell.Tests;

/// <summary>
/// Three things the 0.7.0 testing round found in tray dragging: the chevron vanishing once every
/// icon was on the bar by choice, a menu opened by the press ending the drag, and the drag leaving
/// the foreground on the icon owner's hidden window.
/// </summary>
public class TrayDragFocusTests
{
    // ------------------------------------------------------------------ the chevron

    [Fact]
    public void The_Chevron_Stays_When_Every_Icon_Was_Put_On_The_Bar_One_At_A_Time() =>
        Assert.True(TrayChevronMenu.KeepsChevron(showAll: false, onBar: 5, inMenu: 0));

    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(true, 5, 0)]
    [InlineData(false, 0, 3)]
    [InlineData(false, 2, 3)]
    public void The_Chevron_Stays_Wherever_It_Did_Before(bool showAll, int onBar, int inMenu) =>
        Assert.True(TrayChevronMenu.KeepsChevron(showAll, onBar, inMenu));

    [Fact]
    public void With_No_Icon_To_Move_There_Is_No_Chevron() =>
        Assert.False(TrayChevronMenu.KeepsChevron(showAll: false, onBar: 0, inMenu: 0));

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(false, 4, 0)]
    [InlineData(false, 0, 4)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 3, 0)]
    public void The_Chevron_Is_Drawn_Exactly_When_Its_Menu_Offers_Something(bool showAll, int onBar, int inMenu) =>
        Assert.Equal(
            TrayChevronMenu.Offers(showAll, onBar, inMenu).Count > 0,
            TrayChevronMenu.KeepsChevron(showAll, onBar, inMenu));

    // ------------------------------------------------------------------ the capture

    [Fact]
    public void A_Drag_Takes_Its_Capture_Back_While_The_Button_Is_Held() =>
        Assert.True(TrayDragFocus.KeepsDragAfterLostCapture(leftButtonDown: true, recapturesSoFar: 0));

    [Fact]
    public void A_Released_Button_Ends_The_Drag() =>
        Assert.False(TrayDragFocus.KeepsDragAfterLostCapture(leftButtonDown: false, recapturesSoFar: 0));

    [Fact]
    public void Something_That_Keeps_Taking_The_Capture_Ends_The_Drag_In_The_End()
    {
        Assert.True(TrayDragFocus.KeepsDragAfterLostCapture(true, TrayDragFocus.MaxRecaptures - 1));
        Assert.False(TrayDragFocus.KeepsDragAfterLostCapture(true, TrayDragFocus.MaxRecaptures));
    }

    // ------------------------------------------------------------------ the foreground

    private static readonly IntPtr Notepad = 0x100;
    private static readonly IntPtr HiddenOwnerWindow = 0x200;
    private const uint NotepadPid = 10, ExplorerPid = 20, OtherPid = 40;

    [Fact]
    public void A_Menu_Left_The_Foreground_On_The_Icons_Owner_And_It_Goes_Back() =>
        Assert.True(TrayDragFocus.RestoresForeground(Notepad, NotepadPid, nowPid: ExplorerPid, ownerPid: ExplorerPid));

    [Fact]
    public void Nothing_Changed_Nothing_Moves() =>
        Assert.False(TrayDragFocus.RestoresForeground(Notepad, NotepadPid, nowPid: NotepadPid, ownerPid: ExplorerPid));

    [Fact]
    public void Something_Else_That_Took_The_Foreground_Keeps_It() =>
        Assert.False(TrayDragFocus.RestoresForeground(Notepad, NotepadPid, nowPid: OtherPid, ownerPid: ExplorerPid));

    [Fact]
    public void A_Window_Of_The_Owner_At_The_Press_Is_Not_Fought_Over() =>
        Assert.False(TrayDragFocus.RestoresForeground(HiddenOwnerWindow, ExplorerPid, nowPid: ExplorerPid, ownerPid: ExplorerPid));

    [Fact]
    public void No_Window_At_The_Press_Means_Nothing_To_Give_Back() =>
        Assert.False(TrayDragFocus.RestoresForeground(IntPtr.Zero, 0, nowPid: ExplorerPid, ownerPid: ExplorerPid));

    [Fact]
    public void An_Owner_Nobody_Knows_Does_Not_Match_A_Window_Nobody_Knows() =>
        Assert.False(TrayDragFocus.RestoresForeground(Notepad, NotepadPid, nowPid: 0, ownerPid: 0));
}
