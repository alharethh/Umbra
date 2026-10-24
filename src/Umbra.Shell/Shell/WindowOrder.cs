using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Rendering;

namespace Umbra.Shell.Shell;

/// <summary>
/// Puts Umbra's own windows where the user can see them.
/// </summary>
/// <remarks>
/// <para>
/// Both problems this solves come from the same place: the bar carries <c>WS_EX_NOACTIVATE</c>, so
/// Umbra is never the foreground process. Windows refuses a foreground change to a process that
/// does not already own the foreground — that restriction exists to stop background applications
/// stealing focus, and we look exactly like one. So a window we open lands wherever the Z-order
/// happens to put it, which is usually behind whatever the user was looking at.
/// </para>
/// <para>
/// The bar cannot simply take focus instead. That is what makes click-to-minimise work at all: if
/// clicking a slot activated Umbra, the application would lose foreground before the handler ran,
/// "is this window already in front?" would always answer no, and clicking the active window's icon
/// would re-activate it rather than minimising it.
/// </para>
/// </remarks>
internal static class WindowOrder
{
    private static readonly IntPtr HwndTopmost = new(-1);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Lifts a popup into the topmost band, above the Start menu.
    /// </summary>
    /// <param name="visual">Anything inside the popup — a <c>ContextMenu</c>, typically.</param>
    /// <remarks>
    /// <para>
    /// A WPF <c>ContextMenu</c> is not part of the window it belongs to; it is its own top-level
    /// window, and it does not inherit the bar's topmost flag. The bar is topmost and the Start
    /// menu is topmost, but the menu popup is not, so right-clicking an icon while the Start menu
    /// is open used to draw the menu underneath it.
    /// </para>
    /// <para>
    /// <c>SWP_NOACTIVATE</c> is not optional. Activating here would hand Umbra the foreground,
    /// which is the one thing the bar must never take.
    /// </para>
    /// <para>
    /// Verify any change to this against the Start menu specifically. It sits high in the topmost
    /// band; an ordinary application window sits below the bar already and will report a pass
    /// whether this works or not.
    /// </para>
    /// </remarks>
    public static void Raise(Visual? visual)
    {
        if (visual is null) return;

        if (PresentationSource.FromVisual(visual) is not HwndSource source)
        {
            BringUpLog.Write("menu raise: no HwndSource — the popup has no window of its own yet");
            return;
        }

        if (source.Handle == IntPtr.Zero)
        {
            BringUpLog.Write("menu raise: HwndSource with a null handle");
            return;
        }

        // Only the failure is logged. This runs on every menu the bar opens, and a line per
        // right-click would bury everything else in a log that already carries motion ticks.
        if (!SetWindowPos(source.Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate))
            BringUpLog.Write($"menu raise: SetWindowPos refused hwnd 0x{source.Handle.ToInt64():X}");
    }

    /// <summary>Shows a window and puts it in front of whatever the user was looking at.</summary>
    public static void ShowInFront(this Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Before Show, so the caption is themed by the time it is first painted rather than
        // flashing white and correcting itself.
        DarkTitleBar.Apply(window);

        window.Show();
        BringToFront(window);
    }

    /// <summary>
    /// Lets the process owning <paramref name="window"/> take the foreground for itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what a tray icon's menu needs, and why one misbehaves under this bar.</b> An
    /// application showing a menu from its notification icon is required to make its own window
    /// the foreground first — Microsoft has documented that since KB135788 — because a menu whose
    /// owner is not foreground does not dismiss when the user clicks away, and closes on its own
    /// a few seconds later. Both symptoms were reported here, together.
    /// </para>
    /// <para>
    /// The application does make that call; Windows refuses it. A process may only take the
    /// foreground if the process that currently holds it grants permission, and clicking our bar
    /// never moves the foreground at all, so nobody ever grants anything. On the real taskbar the
    /// click hands Explorer the foreground and Explorer hands it on.
    /// </para>
    /// <para>
    /// So we do what Explorer does. <c>AllowSetForegroundWindow</c> is the documented way to pass
    /// that right to another process, and it may only be called by whoever holds the foreground —
    /// which we do not, hence the same <c>AttachThreadInput</c> borrow used above. <b>We are not
    /// raising the application ourselves</b>: this only makes its own call legal, so an
    /// application that would not have come forward still does not.
    /// </para>
    /// <para>
    /// <b>The borrow itself can leave the bar in front</b>, and that is put back here. Measured
    /// on the 150% laptop with Windows Terminal in front: a press on an icon whose owner raises
    /// nothing, left or right, ended with the bar's own window as the foreground 9 times in 9, so
    /// the next keystroke went nowhere. With Notepad in front it did not happen. The foreground
    /// is already ours while attached, so it is the attach that moves it, not the owner; it is
    /// handed straight back to the window that had it, before the detach. Owners that do take the
    /// foreground still can: see <see cref="ShouldHandBack"/>.
    /// </para>
    /// </remarks>
    public static bool AllowForeground(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;

        uint target = GetWindowThreadProcessId(window, out uint targetProcess);
        if (target == 0 || targetProcess == 0) return false;

        IntPtr foreground = GetForegroundWindow();
        uint foregroundProcess = 0;
        uint theirs = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out foregroundProcess);
        uint ours = GetCurrentThreadId();
        uint ourProcess = (uint)Environment.ProcessId;

        bool attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);

        try
        {
            bool granted = AllowSetForegroundWindow(targetProcess);

            // The attach is what moves the foreground, so it is read here, and handed back while
            // still attached: done after the detach, SetForegroundWindow returned false every time.
            IntPtr now = GetForegroundWindow();
            uint nowProcess = 0;
            if (now != IntPtr.Zero) GetWindowThreadProcessId(now, out nowProcess);

            if (attached && ShouldHandBack(foreground, foregroundProcess, now, nowProcess, ourProcess))
            {
                bool back = SetForegroundWindow(foreground);
                BringUpLog.Write(
                    $"tray press: the foreground borrow moved it to the bar; handed back to 0x{foreground.ToInt64():X}, ok={back}");
            }

            return granted;
        }
        finally
        {
            if (attached) AttachThreadInput(ours, theirs, false);
        }
    }

    /// <summary>
    /// Whether the borrow in <see cref="AllowForeground"/> moved the foreground onto one of our
    /// own windows, so that it should be returned to the window that had it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a move <em>from someone else's window to ours</em> counts. A foreground that was
    /// already ours — the settings window, say — is left where it is, and so is one that moved to
    /// a third process, which can only be the owner using its grant, or the user.
    /// </para>
    /// <para>
    /// Restoring at once rather than after a delay is safe for owners: the grant is made before
    /// the hand-back and survives it. A WinForms test icon with a menu still took the foreground
    /// and showed its menu on every right click (5 in 5, Terminal and Notepad in front).
    /// </para>
    /// </remarks>
    internal static bool ShouldHandBack(
        IntPtr before, uint beforeProcess, IntPtr after, uint afterProcess, uint ourProcess) =>
        before != IntPtr.Zero
        && beforeProcess != 0
        && beforeProcess != ourProcess
        && after != before
        && after != IntPtr.Zero
        && afterProcess == ourProcess;

    /// <summary>
    /// Puts a window belonging to <em>another process</em> in front, past the foreground lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same trick as <see cref="BringToFront"/> and for the same reason, but aimed at the user's
    /// own application rather than at one of ours. It exists because clicking an icon while one of
    /// Windows' flyouts is open did nothing: the flyout owns the foreground, the bar carries
    /// <c>WS_EX_NOACTIVATE</c> so clicking it never moves the foreground, and Windows then refuses
    /// our activation of the user's window because we are not the foreground process. The icons
    /// looked dead and the panel would not go away — one cause, two symptoms.
    /// </para>
    /// <para>
    /// There is no <c>Topmost</c> fallback here. Forcing another application permanently to the
    /// top of the ordinary band would outlive the click, and this is best-effort by design: the
    /// caller dismisses the flyout first, which on its own restores the foreground in the ordinary
    /// case.
    /// </para>
    /// </remarks>
    /// <returns>True when the window took the foreground.</returns>
    public static bool ForceForeground(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return false;

        IntPtr foreground = GetForegroundWindow();
        if (foreground == handle) return true;

        uint theirs = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint ours = GetCurrentThreadId();

        bool attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);

        try
        {
            BringWindowToTop(handle);
            return SetForegroundWindow(handle);
        }
        finally
        {
            if (attached) AttachThreadInput(ours, theirs, false);
        }
    }

    /// <summary>
    /// Brings an already-shown window to the front and gives it the keyboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attaching our input queue to the foreground thread's is what makes
    /// <c>SetForegroundWindow</c> legal: while attached, the two threads share an input state and
    /// Windows treats the call as coming from the foreground process. It is the documented way
    /// round the restriction and it is detached again immediately — leaving it attached would tie
    /// our message loop to another application's.
    /// </para>
    /// <para>
    /// The fallback matters, because the attach can fail — the foreground window may belong to an
    /// elevated process, or to no thread at all if the desktop is in transition. Toggling
    /// <c>Topmost</c> reorders the window to the top of the ordinary band without asking anyone's
    /// permission, so the window is at least <i>visible</i> even when it cannot be given focus.
    /// That is the difference between a settings window the user has to hunt for and one they
    /// merely have to click.
    /// </para>
    /// </remarks>
    public static void BringToFront(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        IntPtr foreground = GetForegroundWindow();
        uint theirs = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint ours = GetCurrentThreadId();

        bool attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        bool raised;

        try
        {
            BringWindowToTop(handle);
            raised = SetForegroundWindow(handle);
        }
        finally
        {
            if (attached) AttachThreadInput(ours, theirs, false);
        }

        if (raised)
        {
            window.Activate();
            return;
        }

        // Front of the ordinary band, then straight back out of the topmost one: the window should
        // be in front now, not permanently on top of everything the user opens afterwards.
        window.Topmost = true;
        window.Topmost = false;

        BringUpLog.Write($"'{window.Title}' could not take foreground (attached={attached}); raised by topmost toggle");
    }
}
