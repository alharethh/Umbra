using Umbra.Shell.Shell;
using Kind = Umbra.Shell.Shell.HiddenTaskbarFocus.Kind;

namespace Umbra.Shell.Tests;

/// <summary>
/// Win+T, Win+B and Win+D's second press hand the keyboard to Explorer's hidden taskbar, where
/// Enter launches apps from Windows' pinned list. The bar gives the foreground back.
/// </summary>
public class HiddenTaskbarFocusTests
{
    private static readonly IntPtr App = 0x100;
    private static readonly IntPtr Tray = 0x200;
    private static readonly IntPtr Desktop = 0x300;

    private sealed class Desk
    {
        public readonly Dictionary<IntPtr, Kind> Kinds = new() { [App] = Kind.Ordinary, [Tray] = Kind.HiddenExplorerTaskbar };
        public readonly HashSet<IntPtr> Gone = [];
        public readonly List<IntPtr> HandedTo = [];
        public long Now;

        public HiddenTaskbarFocus Watcher() => new(
            w => Kinds.TryGetValue(w, out Kind k) ? k : Kind.Other,
            w => !Gone.Contains(w),
            w => { HandedTo.Add(w); return true; },
            () => Desktop,
            () => Now);
    }

    [Fact]
    public void A_Hidden_Explorer_Taskbar_In_Front_Gives_The_Keyboard_Back_To_The_Previous_Window()
    {
        var desk = new Desk();
        var watcher = desk.Watcher();

        watcher.Poll(App);
        Assert.Equal(IntPtr.Zero, watcher.Poll(Tray));       // one tick: could be Start passing through
        Assert.Equal(App, watcher.Poll(Tray));
        Assert.Equal([App], desk.HandedTo);
    }

    [Fact]
    public void A_Single_Tick_Is_Never_Acted_On()
    {
        var desk = new Desk();
        var watcher = desk.Watcher();

        watcher.Poll(App);
        watcher.Poll(Tray);
        watcher.Poll(App);
        watcher.Poll(Tray);

        Assert.Empty(desk.HandedTo);
    }

    [Fact]
    public void The_Desktop_Takes_It_When_The_Previous_Window_Has_Gone()
    {
        var desk = new Desk();
        var watcher = desk.Watcher();

        watcher.Poll(App);
        desk.Gone.Add(App);
        watcher.Poll(Tray);

        Assert.Equal(Desktop, watcher.Poll(Tray));
    }

    [Fact]
    public void It_Stops_After_Three_Hand_Backs_Rather_Than_Fight_Explorer()
    {
        var desk = new Desk();
        var watcher = desk.Watcher();
        watcher.Poll(App);

        for (int i = 0; i < 20; i++) watcher.Poll(Tray);
        Assert.Equal(HiddenTaskbarFocus.MaxHandBacks, desk.HandedTo.Count);

        desk.Now += HiddenTaskbarFocus.HandBackWindowMs;
        watcher.Poll(Tray);
        watcher.Poll(Tray);
        Assert.Equal(HiddenTaskbarFocus.MaxHandBacks + 1, desk.HandedTo.Count);
    }

    [Theory]
    [InlineData("Shell_TrayWnd", false, false, (int)Kind.HiddenExplorerTaskbar)]
    [InlineData("Shell_SecondaryTrayWnd", false, false, (int)Kind.HiddenExplorerTaskbar)]
    [InlineData("Shell_TrayWnd", true, false, (int)Kind.Other)]        // Explorer's own, shown: the user's
    [InlineData("Progman", true, true, (int)Kind.Other)]               // desktop: not a preferred target
    [InlineData("WorkerW", true, false, (int)Kind.Other)]
    [InlineData("Chrome_WidgetWin_1", true, false, (int)Kind.Ordinary)]
    [InlineData("Chrome_WidgetWin_1", false, false, (int)Kind.Other)]
    public void Classifies_By_Class_And_Visibility(string cls, bool visible, bool shell, int expected) =>
        Assert.Equal((Kind)expected, HiddenTaskbarFocus.ClassifyBy(cls, visible, shell));
}
