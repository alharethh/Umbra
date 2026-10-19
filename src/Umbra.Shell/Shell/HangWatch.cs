using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Decides when a bar that is still alive has stopped answering for long enough that the guard
/// should end it. The counting is here, apart from the window messages, so it can be tested
/// without a process to hang.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hung bar is worse than a dead one, and the crash guard could not see it.</b> Measured on
/// the second machine (2026-10-19, a Debug fault hook that sleeps the UI thread): the bar froze
/// with Explorer's taskbar still suppressed, and stayed that way for as long as nobody killed it.
/// The guard was waiting on a process that had not exited. Worse, the frozen bar owns a
/// <c>Shell_TrayWnd</c>, and <c>SHAppBarMessage</c> from <em>any</em> application is delivered
/// there: QA's probe blocked on its <c>ABM_GETSTATE</c> and never returned. And
/// <c>Process.Responding</c> said <c>True</c> the whole time, two minutes in, because it asks
/// the process's main window, which for the bar is not the one on the hung thread.
/// </para>
/// <para>
/// So the guard asks the bar's own windows directly, every <see cref="Interval"/>, and only a
/// run of <see cref="Strikes"/> consecutive failures counts. One slow answer is a busy bar; 10 to
/// 15 s of none is a bar that is not coming back. A bar under a debugger is never counted,
/// because a breakpoint looks exactly like this.
/// </para>
/// <para>
/// <b>Only silences the guard was awake to hear count.</b> A healthy bar can stop answering for
/// longer than that: resuming from sleep, a graphics driver reset while WPF waits on its render
/// thread, a cold sign-in. The first leaves a mark the guard can read, because the guard was
/// frozen too: two checks that should be ~5 s apart are minutes apart on
/// <see cref="Environment.TickCount64"/>, which keeps counting through sleep. A gap longer than
/// <see cref="Gap"/> clears the count and starts <see cref="Grace"/>, during which silences are
/// not counted. The same holds for a guard that was merely not scheduled for that long: whatever
/// stalled it may have stalled the bar, and it heard nothing either way. The other two causes
/// leave no such mark, which is why the guard also relaunches a bar it ended
/// (<see cref="CrashGuard"/>): that bounds the cost of every false positive, including ones
/// nobody has thought of, where a gap test bounds only the one it recognises.
/// </para>
/// <para>
/// <b>The threshold was deliberately not raised.</b> A real hang blocks every
/// <c>SHAppBarMessage</c> on the desktop, not just the bar, so each extra second of patience is
/// paid by every other application, and it is already twice Windows' own five seconds.
/// </para>
/// </remarks>
internal sealed class HangWatch
{
    /// <summary>How often the guard asks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>How long one question may take before it counts as unanswered.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Consecutive unanswered questions before the bar is ended.</summary>
    public const int Strikes = 3;

    /// <summary>
    /// A gap between two checks longer than this means the guard itself was not running: the
    /// machine slept, or was too stalled to schedule it. A normal gap is one interval plus at
    /// most one <see cref="Timeout"/>.
    /// </summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(15);

    /// <summary>How long after such a gap silences are not counted.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    private readonly Func<long> _clock;
    private long _last;
    private long _quietUntil = long.MinValue;
    private int _strikes;

    /// <param name="clock">
    /// Milliseconds that keep counting through sleep: <see cref="Environment.TickCount64"/>
    /// unless a test passes its own.
    /// </param>
    public HangWatch(Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _last = _clock();
    }

    /// <summary>Records one answer, or its absence. True once the bar should be ended.</summary>
    public bool Record(bool answered)
    {
        long now = _clock();
        long since = now - _last;
        _last = now;

        if (since > (long)Gap.TotalMilliseconds)
        {
            _strikes = 0;
            _quietUntil = now + (long)Grace.TotalMilliseconds;
            BringUpLog.Write(
                $"crash guard: {since / 1000} s since the last check, so the guard was not running; " +
                $"not counting silences for {Grace.TotalSeconds:0} s");
        }

        _strikes = answered || now < _quietUntil ? 0 : _strikes + 1;
        return _strikes >= Strikes;
    }

    /// <summary>
    /// Whether the bar's UI thread answered: <c>WM_NULL</c> to its <c>Shell_TrayWnd</c> if it
    /// hosts one, which is the window other applications block on, otherwise to its first
    /// visible top-level window. A bar with no window yet, or one being debugged, answers.
    /// </summary>
    public static bool Answers(Process bar)
    {
        try
        {
            if (CheckRemoteDebuggerPresent(bar.Handle, out bool debugged) && debugged) return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }

        IntPtr window = WindowOf((uint)bar.Id);
        if (window == IntPtr.Zero) return true;

        return SendMessageTimeout(window, WmNull, IntPtr.Zero, IntPtr.Zero,
                                  SmtoAbortIfHung | SmtoBlock, (uint)Timeout.TotalMilliseconds,
                                  out _) != IntPtr.Zero;
    }

    private static IntPtr WindowOf(uint processId)
    {
        IntPtr tray = IntPtr.Zero, visible = IntPtr.Zero;
        StringBuilder name = new(64);

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != processId) return true;

            name.Clear();
            GetClassName(window, name, name.Capacity);
            if (name.ToString() == "Shell_TrayWnd") { tray = window; return false; }
            if (visible == IntPtr.Zero && IsWindowVisible(window)) visible = window;
            return true;
        }, IntPtr.Zero);

        return tray != IntPtr.Zero ? tray : visible;
    }

    /// <summary>
    /// Waits for the bar to exit, ending it first if it stops answering. Returns once it is gone:
    /// true if it was ended here, false if it exited by itself.
    /// </summary>
    /// <param name="interval">Between checks: <see cref="Interval"/> unless a test is in a hurry.</param>
    public static bool WaitForExitOrHang(Process bar, TimeSpan? interval = null)
    {
        HangWatch watch = new();

        while (!bar.WaitForExit(interval ?? Interval))
        {
            if (!watch.Record(Answers(bar))) continue;

            BringUpLog.Write(
                $"crash guard: bar {bar.Id} has not answered for {Strikes} checks in a row; ending it");
            try
            {
                bar.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                BringUpLog.Write(e, "crash guard: could not end the hung bar");
            }

            bar.WaitForExit();
            return true;
        }

        return false;
    }

    private const uint WmNull = 0x0000;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr process, out bool present);
}
