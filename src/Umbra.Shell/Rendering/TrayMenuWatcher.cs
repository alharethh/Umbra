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
    public void Watch(IntPtr iconWindow)
    {
        if (iconWindow == IntPtr.Zero) return;

        GetWindowThreadProcessId(iconWindow, out uint pid);
        if (pid == 0) return;

        _pid = pid;
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
        if (_menu == IntPtr.Zero) return false;

        if (!IsWindow(_menu) || !IsWindowVisible(_menu))
        {
            _menu = IntPtr.Zero;
            return false;
        }

        // Posted rather than sent: it is another application's UI thread and we have no business
        // blocking on it. WM_CLOSE is what the window would get if the user dismissed it.
        PostMessage(_menu, WmClose, IntPtr.Zero, IntPtr.Zero);
        BringUpLog.Write($"tray menu closed: 0x{_menu.ToInt64():X}");

        _menu = IntPtr.Zero;
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
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExAppWindow = 0x00040000;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

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
