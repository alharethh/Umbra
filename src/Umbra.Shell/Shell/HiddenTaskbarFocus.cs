using System.Runtime.InteropServices;
using System.Text;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Hands the keyboard back when Explorer's own, hidden taskbar takes the foreground.
/// </summary>
/// <remarks>
/// <para>
/// Explorer keeps its taskbar hotkeys while ManagedShell hides the taskbar. <b>Win+T and Win+B</b>
/// make the hidden <c>Shell_TrayWnd</c> the foreground, and so does the second <b>Win+D</b>: the
/// window stays invisible, Explorer draws its button tooltip over Umbra's bar naming a button Umbra
/// does not show, Enter launches whatever is first on <em>Windows'</em> pinned list, and Esc does
/// not give the foreground back. Measured with SendInput, director QA <c>winkeys-1020b</c>.
/// </para>
/// <para>
/// So when the foreground is a hidden Explorer taskbar for two ticks running, the last ordinary
/// window that had it gets it back. <b>No hook and no new wake-up</b>: the proximity tick already
/// reads the foreground for the running indicator (§6.6). Two ticks rather than one because Start,
/// Quick Settings and Win+D all pass through Explorer, and a hand-back racing one of those could
/// close the surface the user just asked for. A visible taskbar is never touched — that is
/// Explorer's own, given back by a restore, and keyboard focus in it is the user's.
/// </para>
/// <para>
/// <b>It never fights Explorer.</b> At most <see cref="MaxHandBacks"/> hand-backs in any
/// <see cref="HandBackWindowMs"/>; past that it logs once and leaves the foreground alone until the
/// window has passed. This does not give Umbra keyboard navigation, which the owner deferred; it
/// only stops an invisible taskbar from taking keystrokes.
/// </para>
/// <para>
/// <b>Explorer's hidden <c>SystemTray_Main</c> counts too, but never while it has a menu up.</b>
/// A right click on the battery makes that invisible window the foreground for the power menu,
/// and after Esc the foreground stays there, so Win+T and Win+D from it reach the hidden taskbar
/// as before (review of PR #6, backlog 9b). While the menu is open the same window is the
/// foreground, so its thread's menu mode (<c>GetGUIThreadInfo</c>: <c>GUI_INMENUMODE</c> or
/// <c>GUI_POPUPMENUMODE</c>) decides. Measured on the 150% laptop, sampling every 2 ms across five
/// right clicks: the foreground and <c>flags = 0x14</c> arrived in the same sample every time, and
/// the flags went to 0 with Esc while the foreground stayed. The check is applied to the hidden
/// taskbar as well, so no menu Explorer opens on either window is ever pulled out from under the user.
/// </para>
/// </remarks>
internal sealed class HiddenTaskbarFocus
{
    /// <summary>What the watcher needs to know about one window.</summary>
    internal enum Kind
    {
        /// <summary>None, our own, a tray, the desktop, or not shown: never a hand-back target.</summary>
        Other,

        /// <summary>An ordinary visible application window the keyboard can go back to.</summary>
        Ordinary,

        /// <summary>
        /// Explorer's <c>Shell_TrayWnd</c>, <c>Shell_SecondaryTrayWnd</c> or <c>SystemTray_Main</c>,
        /// hidden, with no menu open on its thread.
        /// </summary>
        HiddenExplorerTaskbar,
    }

    internal const int MaxHandBacks = 3;
    internal const long HandBackWindowMs = 10_000;
    internal const int TicksBeforeHandBack = 2;

    private readonly Func<IntPtr, Kind> _classify;
    private readonly Func<IntPtr, bool> _stillUsable;
    private readonly Func<IntPtr, bool> _handTo;
    private readonly Func<IntPtr> _fallback;
    private readonly Func<long> _nowMs;
    private readonly Queue<long> _recent = new();

    private IntPtr _lastOrdinary;
    private IntPtr _seenTaskbar;
    private int _seenTicks;
    private bool _gaveUpLogged;

    /// <summary>The real thing, against the live desktop.</summary>
    public HiddenTaskbarFocus()
        : this(Classify, StillUsable, WindowOrder.ForceForeground, NativeMethods.GetShellWindow,
               () => Environment.TickCount64)
    {
    }

    /// <summary>For tests: every desktop read and the one write are injected.</summary>
    internal HiddenTaskbarFocus(
        Func<IntPtr, Kind> classify,
        Func<IntPtr, bool> stillUsable,
        Func<IntPtr, bool> handTo,
        Func<IntPtr> fallback,
        Func<long> nowMs)
    {
        _classify = classify;
        _stillUsable = stillUsable;
        _handTo = handTo;
        _fallback = fallback;
        _nowMs = nowMs;
    }

    /// <summary>Looks at the current foreground once. Called from the proximity tick.</summary>
    /// <returns>The window the foreground was handed to, or zero when nothing was done.</returns>
    public IntPtr Poll(IntPtr foreground)
    {
        Kind kind = _classify(foreground);

        if (kind == Kind.Ordinary) _lastOrdinary = foreground;

        if (kind != Kind.HiddenExplorerTaskbar)
        {
            _seenTaskbar = IntPtr.Zero;
            _seenTicks = 0;
            return IntPtr.Zero;
        }

        if (foreground != _seenTaskbar)
        {
            _seenTaskbar = foreground;
            _seenTicks = 0;
        }

        if (++_seenTicks < TicksBeforeHandBack) return IntPtr.Zero;

        long now = _nowMs();
        while (_recent.Count > 0 && now - _recent.Peek() >= HandBackWindowMs) _recent.Dequeue();

        if (_recent.Count >= MaxHandBacks)
        {
            if (!_gaveUpLogged)
            {
                BringUpLog.Write($"hidden taskbar focus: {MaxHandBacks} hand-backs in {HandBackWindowMs} ms, leaving it");
                _gaveUpLogged = true;
            }

            return IntPtr.Zero;
        }

        _gaveUpLogged = false;

        IntPtr target = _lastOrdinary != IntPtr.Zero && _stillUsable(_lastOrdinary) ? _lastOrdinary : _fallback();
        if (target == IntPtr.Zero) return IntPtr.Zero;

        _recent.Enqueue(now);
        _seenTicks = 0;

        bool ok = _handTo(target);
        BringUpLog.Write($"hidden taskbar focus: Explorer's hidden taskbar 0x{foreground:X} had the foreground; " +
                         $"handed to 0x{target:X} ({(target == _lastOrdinary ? "previous window" : "desktop")}) ok={ok}");
        return ok ? target : IntPtr.Zero;
    }

    private static readonly int OurProcessId = Environment.ProcessId;

    internal static Kind Classify(IntPtr window)
    {
        if (window == IntPtr.Zero) return Kind.Other;

        uint thread = NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == OurProcessId) return Kind.Other;

        string cls = ClassOf(window);
        bool visible = NativeMethods.IsWindowVisible(window);
        bool cloaked = visible && IsCloaked(window);

        // The process name is only needed to recognise the notification centre, whose class is
        // shared with every CoreWindow on the desktop, so it is looked up for that class alone.
        string? process = cls == "Windows.UI.Core.CoreWindow" ? ProcessName(pid) : null;

        // Menu mode is only asked of the hidden Explorer windows, the one case it decides.
        bool menuOpen = !visible && IsHiddenExplorerClass(cls) && InMenuMode(thread);

        return ClassifyBy(cls, visible, window == NativeMethods.GetShellWindow(), cloaked, process, menuOpen);
    }

    private static bool IsHiddenExplorerClass(string windowClass) =>
        windowClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or TrayRightClick.ExplorerSystemTrayClass;

    /// <summary>
    /// Whether the thread has a menu up. A failed read counts as a menu: leaving the foreground
    /// where it is costs a tooltip, taking it away from an open menu closes what the user asked for.
    /// </summary>
    private static bool InMenuMode(uint thread)
    {
        var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (thread == 0 || !NativeMethods.GetGUIThreadInfo(thread, ref info)) return true;
        return (info.Flags & (GuiInMenuMode | GuiPopupMenuMode)) != 0;
    }

    private const uint GuiInMenuMode = 0x4;
    private const uint GuiPopupMenuMode = 0x10;

    /// <summary>The rule, free of the desktop. Our own process is filtered out before this.</summary>
    /// <remarks>
    /// <b>A cloaked window or one of Windows' light-dismiss panels is never a hand-back target.</b>
    /// Quick Settings and the notification centre stay visible but cloaked once dismissed; the
    /// first build remembered Quick Settings as the "previous window" and handed Win+T's
    /// foreground to it (review of PR #6).
    /// </remarks>
    internal static Kind ClassifyBy(
        string windowClass, bool visible, bool isShellWindow, bool cloaked = false, string? processName = null,
        bool menuOpen = false)
    {
        if (IsHiddenExplorerClass(windowClass))
            return visible || menuOpen ? Kind.Other : Kind.HiddenExplorerTaskbar;

        // The desktop is not somewhere to hand the keyboard back to by preference: after Win+D's
        // second press the windows are back, and the one the user had is the right answer.
        if (isShellWindow || windowClass is "Progman" or "WorkerW") return Kind.Other;

        if (cloaked || ShellSurfaceWatcher.SurfaceFor(windowClass, processName) != ShellSurface.None)
            return Kind.Other;

        return visible ? Kind.Ordinary : Kind.Other;
    }

    private static bool StillUsable(IntPtr window) =>
        NativeMethods.IsWindow(window) && !NativeMethods.IsIconic(window) && Classify(window) == Kind.Ordinary;

    private static bool IsCloaked(IntPtr window) =>
        NativeMethods.DwmGetWindowAttribute(window, DwmwaCloaked, out int value, sizeof(int)) == 0 && value != 0;

    private const int DwmwaCloaked = 14;

    private static string? ProcessName(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ClassOf(IntPtr window)
    {
        var buffer = new StringBuilder(64);
        return NativeMethods.GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetShellWindow();

        [StructLayout(LayoutKind.Sequential)]
        internal struct GuiThreadInfo
        {
            public int Size;
            public uint Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }
}
