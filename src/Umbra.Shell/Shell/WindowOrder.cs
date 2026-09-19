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
