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
/// reads the foreground for the running indicator (Â§6.6). Two ticks rather than one because Start,
/// Quick Settings and Win+D all pass through Explorer, and a hand-back racing one of those could
/// close the surface the user just asked for. A visible taskbar is never touched â€” that is
/// Explorer's own, given back by a restore, and keyboard focus in it is the user's.
/// </para>
/// <para>
/// <b>It never fights Explorer.</b> At most <see cref="MaxHandBacks"/> hand-backs in any
/// <see cref="HandBackWindowMs"/>; past that it logs once and leaves the foreground alone until the
/// window has passed. This does not give Umbra keyboard navigation, which the owner deferred; it
/// only stops an invisible taskbar from taking keystrokes.
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

        /// <summary>Explorer's <c>Shell_TrayWnd</c> or <c>Shell_SecondaryTrayWnd</c>, hidden.</summary>
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

        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == OurProcessId) return Kind.Other;

        string cls = ClassOf(window);
        bool visible = NativeMethods.IsWindowVisible(window);

        return ClassifyBy(cls, visible, window == NativeMethods.GetShellWindow());
    }

    /// <summary>The rule, free of the desktop. Our own process is filtered out before this.</summary>
    internal static Kind ClassifyBy(string windowClass, bool visible, bool isShellWindow)
    {
        if (windowClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return visible ? Kind.Other : Kind.HiddenExplorerTaskbar;

        // The desktop is not somewhere to hand the keyboard back to by preference: after Win+D's
        // second press the windows are back, and the one the user had is the right answer.
        if (isShellWindow || windowClass is "Progman" or "WorkerW") return Kind.Other;

        return visible ? Kind.Ordinary : Kind.Other;
    }

    private static bool StillUsable(IntPtr window) =>
        NativeMethods.IsWindow(window) && NativeMethods.IsWindowVisible(window) && !NativeMethods.IsIconic(window);

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
    }
}
