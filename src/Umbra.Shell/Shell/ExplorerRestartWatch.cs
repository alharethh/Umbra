using System.Runtime.InteropServices;
using Umbra.Core.Layout;

namespace Umbra.Shell.Shell;

/// <summary>What a reservation check decided.</summary>
internal enum ReservationVerdict
{
    /// <summary>Not armed: nothing to do.</summary>
    Idle,

    /// <summary>Armed and the reservation still holds; look again on the next tick.</summary>
    KeepWatching,

    /// <summary>The reservation is gone. Re-register the AppBar, then keep watching.</summary>
    Reregister,

    /// <summary>Done: the reservation held to the end of the watch, or came back.</summary>
    Settled,

    /// <summary>Re-registering did not bring it back. Stop rather than loop.</summary>
    GaveUp,
}

/// <summary>
/// Decides when the bar has to re-register its AppBar because Explorer restarted underneath it.
/// </summary>
/// <remarks>
/// <para>
/// A restarted Explorer forgets every AppBar that the old one held. The bar stays drawn where it
/// was and keeps answering <c>WindowFromPoint</c>, but the work area goes back to the whole screen,
/// so maximised windows run underneath it. Explorer announces itself with <c>TaskbarCreated</c>,
/// and that is the trigger.
/// </para>
/// <para>
/// <b>The trigger alone is not enough, because the bar sends it too.</b> <c>TrayRefresh</c>
/// broadcasts <c>TaskbarCreated</c> at startup, again five seconds later, and from the menu. Every
/// top-level window receives a broadcast, the bar's own included, so a handler that re-registered on
/// the message would reflow every maximised window on the desktop twice per launch, which is the
/// steady-state reflow §6.3 forbids. Two independent guards, each sufficient on its own for the
/// bar's own broadcasts:
/// </para>
/// <list type="bullet">
/// <item>The <b>shell process must have changed</b>. The owner of <c>GetShellWindow</c> is the
/// shell Explorer, and its process id is the same before and after any broadcast of ours.</item>
/// <item>The <b>reservation must be verifiably missing</b>: the bar's monitor's work area overlaps
/// the strip the bar occupies. A held reservation is never re-registered, whatever the trigger.</item>
/// </list>
/// <para>
/// <b>Why it watches for a while rather than looking once.</b> A starting Explorer reserves the
/// bottom of the screen for its own taskbar until ManagedShell's watchdog autohides it, and on a
/// screen where both bars are the same height that reads, for a moment, exactly like our
/// reservation being held. So the check runs once a second for <see cref="MaxChecks"/> seconds and
/// acts on the first tick that sees it missing. QA saw the work area at the full screen from +5 s
/// on, so twenty seconds is generous.
/// </para>
/// <para>
/// An unknown shell id (zero: no shell window at that moment) arms the watch rather than ignoring
/// it. The reservation check still stands between it and any reflow.
/// </para>
/// </remarks>
internal sealed class ExplorerRestartWatch
{
    /// <summary>How many one-second checks follow a shell change.</summary>
    public const int MaxChecks = 20;

    /// <summary>Re-registrations allowed per shell change before the watch gives up.</summary>
    public const int MaxReregistrations = 2;

    private uint _knownShell;
    private int _checks;
    private int _reregistrations;

    public ExplorerRestartWatch(uint shellProcessId) => _knownShell = shellProcessId;

    /// <summary>Whether a watch is in progress.</summary>
    public bool Armed { get; private set; }

    /// <summary>The shell process the bar last registered with.</summary>
    public uint KnownShell => _knownShell;

    /// <summary>
    /// Called on <c>TaskbarCreated</c>. Returns true when it armed a watch, false when the shell is
    /// the one the bar already registered with — which is what the bar's own broadcasts look like.
    /// </summary>
    public bool OnTaskbarCreated(uint shellProcessId)
    {
        if (Armed) return false;
        if (shellProcessId != 0 && shellProcessId == _knownShell) return false;

        Armed = true;
        _checks = 0;
        _reregistrations = 0;
        return true;
    }

    /// <summary>One tick of the watch.</summary>
    public ReservationVerdict Check(uint shellProcessId, bool reservationHeld)
    {
        if (!Armed) return ReservationVerdict.Idle;

        _checks++;

        if (!reservationHeld)
        {
            if (_reregistrations >= MaxReregistrations) return Disarm(ReservationVerdict.GaveUp, shellProcessId);

            _reregistrations++;
            if (shellProcessId != 0) _knownShell = shellProcessId;
            return ReservationVerdict.Reregister;
        }

        // Held after a re-registration: it worked, and there is nothing more to wait for.
        if (_reregistrations > 0) return Disarm(ReservationVerdict.Settled, shellProcessId);

        return _checks >= MaxChecks
            ? Disarm(ReservationVerdict.Settled, shellProcessId)
            : ReservationVerdict.KeepWatching;
    }

    private ReservationVerdict Disarm(ReservationVerdict verdict, uint shellProcessId)
    {
        Armed = false;
        if (shellProcessId != 0) _knownShell = shellProcessId;
        return verdict;
    }

    /// <summary>
    /// Whether the work area leaves out the strip the bar occupies on its edge.
    /// </summary>
    /// <remarks>
    /// Both rectangles in the same units, physical pixels as Win32 reports them. Only the edge's own
    /// side is compared: another AppBar stacked further in moves the work area further away from the
    /// bar, which still counts as held.
    /// </remarks>
    public static bool ReservationHeld(Rect workArea, Rect bar, BarEdge edge) => edge switch
    {
        BarEdge.Bottom => workArea.Bottom <= bar.Top,
        BarEdge.Top => workArea.Top >= bar.Bottom,
        BarEdge.Left => workArea.Left >= bar.Right,
        BarEdge.Right => workArea.Right <= bar.Left,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    /// <summary>The shell Explorer's process id, or zero when there is no shell window.</summary>
    public static uint ShellProcessId()
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) return 0;

        GetWindowThreadProcessId(shell, out uint pid);
        return pid;
    }

    /// <summary>
    /// Reads the work area of the window's monitor and the window's own rectangle, both physical.
    /// </summary>
    public static bool TryMeasure(IntPtr window, out Rect workArea, out Rect bar)
    {
        workArea = default;
        bar = default;

        IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        MONITORINFO info = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(window, out RECT rect))
            return false;

        workArea = ToRect(info.rcWork);
        bar = ToRect(rect);
        return true;
    }

    private static Rect ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
