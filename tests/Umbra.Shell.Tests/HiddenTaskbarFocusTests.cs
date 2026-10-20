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

    /// <summary>
    /// Review of PR #6: with Quick Settings open, Win+T handed the foreground to
    /// <c>ControlCenterWindow</c>, remembered as the previous window. Cloaked windows and Windows'
    /// light-dismiss panels are never a target.
    /// </summary>
    [Theory]
    [InlineData("ControlCenterWindow", false, null)]                          // Quick Settings
    [InlineData("Windows.UI.Core.CoreWindow", false, "ShellExperienceHost")]  // notification centre
    [InlineData("NotificationCenterWindow", false, null)]
    [InlineData("Chrome_WidgetWin_1", true, null)]                            // cloaked app window
    public void Shell_Panels_And_Cloaked_Windows_Are_Not_Hand_Back_Targets(string cls, bool cloaked, string? process) =>
        Assert.Equal(Kind.Other, HiddenTaskbarFocus.ClassifyBy(cls, true, false, cloaked, process));

    /// <summary>
    /// Backlog 9b: after a battery right-click and Esc the foreground stays on Explorer's hidden
    /// <c>SystemTray_Main</c>, and Win+T from there reaches the hidden taskbar. While the power
    /// menu is open the same window is the foreground, and it must be left alone.
    /// </summary>
    [Theory]
    [InlineData("SystemTray_Main", false, false, (int)Kind.HiddenExplorerTaskbar)]   // after Esc
    [InlineData("SystemTray_Main", false, true, (int)Kind.Other)]                    // power menu open
    [InlineData("SystemTray_Main", true, false, (int)Kind.Other)]
    [InlineData("Shell_TrayWnd", false, true, (int)Kind.Other)]                      // a menu on the hidden taskbar
    public void Explorers_Hidden_Tray_Host_Is_Handed_Back_Only_With_No_Menu_Open(
        string cls, bool visible, bool menuOpen, int expected) =>
        Assert.Equal((Kind)expected, HiddenTaskbarFocus.ClassifyBy(cls, visible, false, menuOpen: menuOpen));

    [Fact]
    public void The_Power_Menu_Is_Never_Taken_Away_And_Esc_Then_Hands_Back()
    {
        var desk = new Desk();
        var watcher = desk.Watcher();
        IntPtr trayHost = 0x400;

        watcher.Poll(App);
        desk.Kinds[trayHost] = HiddenTaskbarFocus.ClassifyBy("SystemTray_Main", false, false, menuOpen: true);
        for (int i = 0; i < 10; i++) watcher.Poll(trayHost);
        Assert.Empty(desk.HandedTo);

        desk.Kinds[trayHost] = HiddenTaskbarFocus.ClassifyBy("SystemTray_Main", false, false, menuOpen: false);
        Assert.Equal(IntPtr.Zero, watcher.Poll(trayHost));
        Assert.Equal(App, watcher.Poll(trayHost));
    }

    /// <summary>
    /// Backlog 9d: Win+X's menu is a XAML popup owned by the hidden <c>Shell_TrayWnd</c>, and the
    /// thread's menu flags stay at zero while it is up, so a menu-mode check alone handed the
    /// keyboard back and closed the menu. Real windows on a thread with no menu: an owned, shown
    /// popup counts as a menu; a hidden one, or a shown window nobody owns, does not.
    /// </summary>
    [Fact]
    public void An_Owned_Popup_On_The_Hidden_Window_Counts_As_An_Open_Menu()
    {
        bool withPopup = false, popupHidden = true, unowned = true;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var owner = Window("owner", IntPtr.Zero, visible: false);
                using var popup = Window("popup", owner.Handle, visible: true);
                uint tid = GetWindowThreadProcessId(owner.Handle, out _);

                withPopup = HiddenTaskbarFocus.IsMenuOpen(owner.Handle, tid);

                ShowWindow(popup.Handle, 0);
                popupHidden = HiddenTaskbarFocus.IsMenuOpen(owner.Handle, tid);

                using var stranger = Window("stranger", IntPtr.Zero, visible: true);
                unowned = HiddenTaskbarFocus.IsMenuOpen(owner.Handle, tid);
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.True(withPopup);
        Assert.False(popupHidden);
        Assert.False(unowned);
    }

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    /// <summary>A tiny top-level window, off screen, that never takes the foreground.</summary>
    private static System.Windows.Interop.HwndSource Window(string name, IntPtr owner, bool visible) =>
        new(new System.Windows.Interop.HwndSourceParameters(name, 1, 1)
        {
            WindowStyle = WsPopup | (visible ? WsVisible : 0),
            ExtendedWindowStyle = WsExToolWindow | WsExNoActivate,
            ParentWindow = owner,
            PositionX = -32000,
            PositionY = -32000,
        });

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [Fact]
    public void An_Ordinary_CoreWindow_Is_Still_Ordinary() =>
        Assert.Equal(Kind.Ordinary, HiddenTaskbarFocus.ClassifyBy("Windows.UI.Core.CoreWindow", true, false, false, "Calculator"));
}
