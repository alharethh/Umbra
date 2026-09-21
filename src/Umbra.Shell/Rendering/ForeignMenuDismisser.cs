using System.Runtime.InteropServices;
using System.Windows;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Closes a menu belonging to <em>another application</em> when the user clicks the bar.
/// </summary>
/// <remarks>
/// <para>
/// Reported as: click a tray icon, its menu opens, click an empty part of the bar and the menu
/// stays there. Clicking anywhere else on the desktop closes it.
/// </para>
/// <para>
/// <b>Not our menu, and not our dismisser's job.</b> A tray icon's menu is put up by the
/// application that owns the icon, usually through <c>TrackPopupMenu</c>, and it closes on an
/// outside click the way every Win32 menu does — when the owner loses activation. Clicking the
/// bar does not do that. The bar carries <c>WS_EX_NOACTIVATE</c>, so the foreground never moves,
/// the owner is never told it lost anything, and the menu sits there. That is the same root cause
/// as the Start menu, Quick Settings and the notification centre, with a fourth cast.
/// </para>
/// <para>
/// <b>An earlier note in this project said third-party menus need nothing from us</b>, on the
/// grounds that <c>TrackPopupMenu</c> takes a global capture so the click is delivered to the
/// menu, which then dismisses itself. That reasoning is why this was never handled, and the
/// report above is the counter-example. It also explains why the fix cannot live in a mouse
/// handler on our window: <b>if the menu really does hold the capture, the press never reaches
/// us to be handled.</b> So this polls instead, from a tick that already runs.
/// </para>
/// <para>
/// <b>The press has to be an edge, not a state.</b> The click that opens a tray menu is over the
/// bar too — that is where the icon is — so cancelling whenever a button is down over the bar
/// would close the menu in the same gesture that opened it, and the menu would appear not to open
/// at all. Only a button going down while a menu is <i>already</i> up counts.
/// </para>
/// </remarks>
internal sealed class ForeignMenuDismisser
{
    private bool _buttonWasDown;

    /// <summary>
    /// Cancels a foreign menu if the user has just pressed a button over the bar.
    /// </summary>
    /// <param name="barBounds">The bar, in physical pixels.</param>
    /// <returns>True when a menu was cancelled.</returns>
    public bool Poll(Rect barBounds)
    {
        bool down = Pressed(VkLButton) || Pressed(VkRButton) || Pressed(VkMButton);

        if (!PressEdge(down)) return false;
        if (!GetCursorPos(out NativePoint cursor)) return false;
        if (!barBounds.Contains(cursor.X, cursor.Y)) return false;

        // Logged on every press that lands on the bar, which is rare enough to afford and is the
        // only way to see what a tray menu actually is. Four fixes were aimed at this by
        // reasoning about it; none of them were aimed at a measurement, and none of them worked.
        // If MenuOwner is 0 here while a tray menu is plainly on screen, the menu is not a Win32
        // menu at all and WM_CANCELMODE was never going to reach it.
        BringUpLog.Write(
            $"bar press | cursor {cursor.X},{cursor.Y} | menu owner 0x{MenuOwner():X} | " +
            $"gui flags 0x{Flags():X} | foreground {ForegroundDescription()}");

        return Cancel();
    }

    /// <summary>The raw <c>GUITHREADINFO</c> flags, for the diagnostic above.</summary>
    private static uint Flags()
    {
        GuiThreadInfo info = new() { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(0, ref info) ? info.Flags : 0;
    }

    /// <summary>The foreground window's class and owning process, for the diagnostic above.</summary>
    private static string ForegroundDescription()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return "none";

        System.Text.StringBuilder name = new(256);
        GetClassName(window, name, name.Capacity);

        string process = "?";

        try
        {
            GetWindowThreadProcessId(window, out uint pid);
            using System.Diagnostics.Process owner = System.Diagnostics.Process.GetProcessById((int)pid);
            process = owner.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
        }

        return $"{name} / {process}";
    }

    /// <summary>
    /// Whether a button has just gone down, as opposed to still being down.
    /// </summary>
    /// <remarks>
    /// Separated out and internal because it is the part that is easy to get wrong and the only
    /// part that can be tested without a menu on screen. Polling the <i>state</i> rather than the
    /// edge would cancel the menu in the same gesture that opened it: the click that opens a tray
    /// menu is over the bar, and the button is still down on the tick after the menu appears.
    /// </remarks>
    internal bool PressEdge(bool buttonDown)
    {
        bool edge = buttonDown && !_buttonWasDown;
        _buttonWasDown = buttonDown;

        return edge;
    }

    /// <summary>Whether the foreground thread is showing a menu.</summary>
    /// <remarks>
    /// Thread zero means "the foreground thread", which is the one that owns any menu the user is
    /// looking at. A menu of our own does not register here: WPF's <c>ContextMenu</c> is an
    /// ordinary popup window rather than a Win32 menu, so the two dismissers cannot fight.
    /// </remarks>
    public static bool IsOpen() => MenuOwner() != IntPtr.Zero;

    private static bool Cancel()
    {
        IntPtr owner = MenuOwner();
        if (owner == IntPtr.Zero) return false;

        // WM_CANCELMODE is what Windows itself sends to end menu mode; the owner's menu loop
        // unwinds and the menu closes without the owner being told anything was clicked. Posted
        // rather than sent, because the owner is inside its own modal loop and a blocking call
        // into it from here would wait for that loop to finish — which is what we are trying to
        // bring about.
        PostMessage(owner, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
        BringUpLog.Write("foreign menu cancelled: a press landed on the bar while it was open");

        return true;
    }

    private static IntPtr MenuOwner()
    {
        GuiThreadInfo info = new() { Size = Marshal.SizeOf<GuiThreadInfo>() };

        // Thread 0 is the foreground thread.
        if (!GetGUIThreadInfo(0, ref info)) return IntPtr.Zero;
        if ((info.Flags & (GuiInMenuMode | GuiPopupMenuMode)) == 0) return IntPtr.Zero;

        return info.MenuOwner;
    }

    private static bool Pressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;

    private const uint WmCancelMode = 0x001F;
    private const uint GuiInMenuMode = 0x00000004;
    private const uint GuiPopupMenuMode = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public NativeRect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
