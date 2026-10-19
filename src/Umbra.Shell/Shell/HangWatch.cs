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
/// run of <see cref="Strikes"/> consecutive failures counts. One slow answer is a busy bar; a
/// quarter of a minute of none is a bar that is not coming back. A bar under a debugger is never
/// counted, because a breakpoint looks exactly like this.
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

    private int _strikes;

    /// <summary>Records one answer, or its absence. True once the bar should be ended.</summary>
    public bool Record(bool answered)
    {
        _strikes = answered ? 0 : _strikes + 1;
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
    /// Waits for the bar to exit, ending it first if it stops answering. Returns once it is gone.
    /// </summary>
    public static void WaitForExitOrHang(Process bar)
    {
        HangWatch watch = new();

        while (!bar.WaitForExit(Interval))
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
            return;
        }
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
