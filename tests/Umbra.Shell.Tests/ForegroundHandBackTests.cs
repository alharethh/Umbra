using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// A press on a notification icon borrows the foreground so the icon's owner may take it, and the
/// borrow itself moved the foreground onto the bar's own window whenever Windows Terminal was in
/// front. An icon that raised nothing then left the bar in front and the next keystroke went
/// nowhere. <see cref="WindowOrder.ShouldHandBack"/> decides when to give it back.
/// </summary>
public class ForegroundHandBackTests
{
    private const uint Ours = 100;
    private const uint Terminal = 200;
    private const uint Owner = 300;

    private static readonly IntPtr TerminalWindow = new(0x1F4069A);
    private static readonly IntPtr BarWindow = new(0x16B06F0);
    private static readonly IntPtr OwnerWindow = new(0x9B066C);

    [Fact]
    public void A_Foreground_The_Borrow_Moved_Onto_The_Bar_Goes_Back() =>
        // The measured case: Terminal before, the bar's own window after.
        Assert.True(WindowOrder.ShouldHandBack(TerminalWindow, Terminal, BarWindow, Ours, Ours));

    [Fact]
    public void A_Foreground_That_Did_Not_Move_Is_Left_Alone() =>
        // Notepad in front: the borrow moved nothing, so there is nothing to give back.
        Assert.False(WindowOrder.ShouldHandBack(TerminalWindow, Terminal, TerminalWindow, Terminal, Ours));

    [Fact]
    public void A_Foreground_The_Owner_Took_Is_Not_Taken_Back() =>
        // The owner using its grant is the point of the grant.
        Assert.False(WindowOrder.ShouldHandBack(TerminalWindow, Terminal, OwnerWindow, Owner, Ours));

    [Fact]
    public void A_Foreground_That_Was_Already_Ours_Stays_Ours() =>
        // The settings window in front, then a tray press: moving between our own windows is not
        // something the borrow did to anyone else.
        Assert.False(WindowOrder.ShouldHandBack(new IntPtr(0x1234), Ours, BarWindow, Ours, Ours));

    [Fact]
    public void No_Foreground_Before_Means_Nothing_To_Return_To()
    {
        Assert.False(WindowOrder.ShouldHandBack(IntPtr.Zero, 0, BarWindow, Ours, Ours));
        Assert.False(WindowOrder.ShouldHandBack(TerminalWindow, 0, BarWindow, Ours, Ours));
    }

    [Fact]
    public void No_Foreground_After_Is_Not_The_Bar() =>
        Assert.False(WindowOrder.ShouldHandBack(TerminalWindow, Terminal, IntPtr.Zero, 0, Ours));
}
