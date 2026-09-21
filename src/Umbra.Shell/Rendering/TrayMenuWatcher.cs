using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Remembers the window an application puts up when its tray icon is clicked, so a click on the
/// bar can close it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A tray icon's menu is usually not a menu.</b> Measured, after four fixes that assumed it
/// was: right-clicking Discord's icon produces
/// <c>Chrome_WidgetWin_1 / Discord [2208,1106 278x273]</c> — an ordinary top-level Chromium
/// window, screenshotted to be sure. <c>GetGUIThreadInfo</c> reports no menu mode and no menu
/// owner the whole time it is up. So <c>WM_CANCELMODE</c> was aimed at something that was never
/// there, and neither was the foreground grant that followed it.
/// </para>
/// <para>
/// It stays open because closing it is the owner's job and the owner has no reason to: these
/// windows close when the application sees the user click somewhere else, and clicking the bar
/// is not somewhere else as far as Windows is concerned — the bar carries
/// <c>WS_EX_NOACTIVATE</c>, so the foreground never moves and nothing is ever notified.
/// </para>
/// <para>
/// <b>There is no general way to recognise such a window, so this does not try.</b> It watches
/// for one appearing in the clicked application's process just after we forwarded the click, and
/// remembers that. Identity by timing rather than by shape, because the shape is whatever the
/// application felt like — Discord's menu carries the same window class as Discord itself.
/// </para>
/// </remarks>
internal sealed class TrayMenuWatcher
{
    /// <summary>
    /// How long after a click a new window is still assumed to be that click's menu.
    /// </summary>
    /// <remarks>
    /// Measured at about a second on this machine for Discord, which is slow because the window
    /// is a Chromium one being created. Sampling stops as soon as something is found.
    /// </remarks>
    private const int SamplesAfterClick = 8;

    private readonly DispatcherTimer _sampler;
    private HashSet<IntPtr> _before = [];
    private uint _pid;
    private int _samples;

    /// <summary>The window most recently identified as a tray menu, if it is still up.</summary>
    private IntPtr _menu;

    /// <summary>What had the foreground before the icon was clicked.</summary>
    /// <remarks>
    /// Recorded so it can be put back, which is the part that generalises. See
    /// <see cref="Close"/>.
    /// </remarks>
    private IntPtr _foregroundBefore;

    /// <summary>Whether the click being watched was a right click. See <see cref="Watch"/>.</summary>
    private bool _menuGesture;

    /// <summary>The icon owner's UI thread, which is the thread any Win32 menu of its belongs to.</summary>
    private uint _thread;

    public TrayMenuWatcher()
    {
        _sampler = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };

        _sampler.Tick += OnSample;
    }

    /// <summary>
    /// Called just after a tray icon's click has been forwarded to its owner.
    /// </summary>
    /// <param name="iconWindow">The window the icon registered with, which names the process.</param>
    /// <param name="menuGesture">
    /// Whether this was the gesture that raises a menu — a right click. It gates the fallback in
    /// <see cref="Close"/> that acts on the owner merely being in front, because a <i>left</i>
    /// click on a tray icon usually restores the application's window instead, and treating that
    /// as an open menu would yank the foreground back off the window the user just asked for.
    /// </param>
    public void Watch(IntPtr iconWindow, bool menuGesture)
    {
        if (iconWindow == IntPtr.Zero) return;

        uint thread = GetWindowThreadProcessId(iconWindow, out uint pid);
        if (pid == 0) return;

        _thread = thread;

        // Whatever the user was in before this click. Captured only if it is not already the
        // application we are about to poke, or restoring it later would be a no-op.
        IntPtr foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        _foregroundBefore = foregroundPid == pid ? IntPtr.Zero : foreground;

        BringUpLog.Write($"tray menu watch: pid {pid}, thread {thread}, gesture={(menuGesture ? "right" : "left")}");

        _pid = pid;
        _menuGesture = menuGesture;
        _menu = IntPtr.Zero;
        _samples = 0;
        _before = VisibleWindowsOf(pid);

        _sampler.Start();
    }

    /// <summary>
    /// Closes the tracked menu, if there is one still on screen.
    /// </summary>
    /// <returns>True when something was closed.</returns>
    public bool Close()
    {
        bool tracked = _menu != IntPtr.Zero && IsWindow(_menu) && IsWindowVisible(_menu);
        if (!tracked) _menu = IntPtr.Zero;

        // <b>Ask the owner's thread about menu mode, not the foreground thread.</b> Steam's tray
        // menu is a real Win32 menu, and this is why two earlier attempts could not see it:
        // GetGUIThreadInfo(0) reports the <i>foreground</i> thread, and a tray menu does not put
        // its owner in front — measured, the foreground stayed on the user's own application the
        // whole time the menu was up, so the flags always read zero. Asked about the owner's own
        // thread, the same call answers correctly.
        IntPtr menuOwner = IntPtr.Zero;

        if (_thread != 0)
        {
            GuiThreadInfo info = new() { Size = Marshal.SizeOf<GuiThreadInfo>() };

            if (GetGUIThreadInfo(_thread, ref info)
                && (info.Flags & (GuiInMenuMode | GuiPopupMenuMode)) != 0)
            {
                menuOwner = info.MenuOwner;
            }
        }

        // Is the application we last forwarded a click to the one in front? If so it very likely
        // has something open, whether or not the sampler managed to identify the window.
        IntPtr foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        bool ownerInFront = _menuGesture && _pid != 0 && foregroundPid == _pid;

        if (!tracked && !ownerInFront && menuOwner == IntPtr.Zero)
        {
            BringUpLog.Write($"tray menu close: nothing open for pid {_pid} (thread {_thread})");
            return false;
        }

        // A genuine Win32 menu unwinds on WM_CANCELMODE, which is what Windows itself sends.
        // Posted, because the owner is inside its own modal loop and a blocking call would wait
        // on the very loop we are trying to end.
        if (menuOwner != IntPtr.Zero)
        {
            PostMessage(menuOwner, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
            BringUpLog.Write($"tray menu cancelled: Win32 menu owned by 0x{menuOwner.ToInt64():X}");
        }

        // <b>Moving the foreground is the part that works for every application, and it is what
        // clicking outside Umbra already does.</b> These menus close when their owner sees the
        // user go somewhere else; the bar carries WS_EX_NOACTIVATE so clicking it moves nothing
        // and the owner never finds out. Putting back whatever had the foreground before the
        // icon was clicked delivers exactly the signal the owner is waiting for, without us
        // having to recognise its menu at all — which matters because there is nothing to
        // recognise: Steam's is a Win32 menu and Discord's is a Chromium window.
        if (_foregroundBefore != IntPtr.Zero && IsWindow(_foregroundBefore))
            Shell.WindowOrder.ForceForeground(_foregroundBefore);

        // And the window itself where one was identified, for an application that ignores the
        // activation change. Posted rather than sent: it is another UI thread and we have no
        // business blocking on it.
        if (tracked)
        {
            PostMessage(_menu, WmClose, IntPtr.Zero, IntPtr.Zero);
            BringUpLog.Write($"tray menu closed: 0x{_menu.ToInt64():X}");
        }
        else
        {
            BringUpLog.Write($"tray menu: no window identified, foreground restored for pid {_pid}");
        }

        _menu = IntPtr.Zero;
        _pid = 0;
        _thread = 0;
        _menuGesture = false;
        return true;
    }

    private void OnSample(object? sender, EventArgs e)
    {
        if (++_samples > SamplesAfterClick)
        {
            _sampler.Stop();
            return;
        }

        foreach (IntPtr window in VisibleWindowsOf(_pid))
        {
            if (_before.Contains(window)) continue;
            if (!LooksLikeAMenu(window)) continue;

            _menu = window;
            _sampler.Stop();

            GetWindowRect(window, out NativeRect r);
            BringUpLog.Write(
                $"tray menu seen: 0x{window.ToInt64():X} {ClassOf(window)} " +
                $"[{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}]");

            return;
        }
    }

    /// <summary>
    /// Whether a newly-appeared window is plausibly a menu rather than the application itself.
    /// </summary>
    /// <remarks>
    /// <b>The guard that matters is <c>WS_POPUP</c> without <c>WS_EX_APPWINDOW</c>.</b> Clicking a
    /// tray icon frequently restores the application's main window instead of opening a menu, and
    /// remembering that as "the menu" would mean the next click on the bar closed the user's
    /// application. A main window is <c>WS_OVERLAPPEDWINDOW</c> and appears on the taskbar; a
    /// menu is a borderless popup that does not. The size bound is a second line of defence for a
    /// borderless application window.
    /// </remarks>
    private static bool LooksLikeAMenu(IntPtr window)
    {
        int style = GetWindowLong(window, GwlStyle);
        int exStyle = GetWindowLong(window, GwlExStyle);

        if ((style & WsPopup) == 0) return false;
        if ((exStyle & WsExAppWindow) != 0) return false;

        if (!GetWindowRect(window, out NativeRect r)) return false;

        int width = r.Right - r.Left, height = r.Bottom - r.Top;

        return width is > 16 and < 900 && height is > 16 and < 1000;
    }

    private static HashSet<IntPtr> VisibleWindowsOf(uint pid)
    {
        HashSet<IntPtr> windows = [];

        EnumWindows(
            (window, _) =>
            {
                if (!IsWindowVisible(window)) return true;

                GetWindowThreadProcessId(window, out uint owner);
                if (owner == pid) windows.Add(window);

                return true;
            },
            IntPtr.Zero);

        return windows;
    }

    private static string ClassOf(IntPtr window)
    {
        StringBuilder name = new(256);
        GetClassName(window, name, name.Capacity);

        return name.ToString();
    }

    private const uint WmClose = 0x0010;
    private const uint WmCancelMode = 0x001F;
    private const uint GuiInMenuMode = 0x00000004;
    private const uint GuiPopupMenuMode = 0x00000010;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExAppWindow = 0x00040000;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
