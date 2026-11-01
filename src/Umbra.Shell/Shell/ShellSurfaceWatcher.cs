using System.Runtime.InteropServices;
using System.Text;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>Windows' own light-dismiss shell surfaces, as far as we can close them.</summary>
internal enum ShellSurface
{
    /// <summary>Nothing of Windows' is in front — or nothing we recognise.</summary>
    None,

    /// <summary>Quick Settings: the panel the volume, network and battery icons raise.</summary>
    QuickSettings,

    /// <summary>The notification centre and calendar, which the clock raises.</summary>
    NotificationCentre,
}

/// <summary>
/// Closes whichever of Windows' own flyouts is open, so a panel of ours does not open on top of
/// one.
/// </summary>
/// <remarks>
/// <para>
/// This is <see cref="StartMenuWatcher"/>'s problem with a second cast of characters, and it has
/// the same cause: these surfaces close themselves when the user clicks outside them, and clicking
/// the bar is not a click as far as Windows is concerned. The bar carries <c>WS_EX_NOACTIVATE</c>,
/// so the foreground never moves and the flyout is never told it lost anything. On the real
/// taskbar, opening the overflow with the volume panel up closes the volume panel; here it left it
/// sitting in front of our flyout.
/// </para>
/// <para>
/// <b>Out-ranking them is not an option, for the reason recorded against the Start menu:</b> they
/// live in a Z-order band above ordinary topmost windows, which needs <c>SetWindowBand</c> and a
/// UIAccess manifest to enter. So, as with Start, the fix is to do what the click should have
/// done — send the surface's own keyboard toggle.
/// </para>
/// <para>
/// <b>Every toggle here is dangerous called blind</b>, exactly as <c>ShowStartMenu</c> is: sending
/// one when the surface is closed <i>opens</i> it. Nothing is sent unless the foreground window's
/// class is one this table names, and the class is re-read at the moment of dismissal rather than
/// trusted from an earlier poll.
/// </para>
/// <para>
/// <b>Third-party tray menus were assumed to need nothing from us. They do</b> — see
/// <see cref="Rendering.TrayMenuWatcher"/>. The assumption was that an application showing its
/// menu through <c>TrackPopupMenu</c> holds a global mouse capture, so the click that would open
/// our panel is delivered to the menu, which dismisses itself. Measured against Steam, Discord
/// and OBS, none of that holds: the capture does not reach us, the menus stay open, and two of
/// the three are not <c>TrackPopupMenu</c> at all. This note is kept because it is the reasoning
/// that left the case unhandled, and it reads entirely plausible.
/// </para>
/// <para>
/// <b>The class table is the part that has to be measured, and it says so when it cannot answer.</b>
/// Quick Settings has been <c>ControlCenterWindow</c> across Windows 11, but the surfaces move
/// between host processes from build to build, and a table that silently matched nothing would be
/// indistinguishable from a working one — the flyout would simply keep opening underneath, which
/// is the bug we started from. So an unrecognised foreground class is logged once, with its
/// process, the first time we are asked to close it.
/// </para>
/// </remarks>
internal sealed class ShellSurfaceWatcher
{
    private readonly HashSet<string> _loggedUnknownClasses = [];

    /// <summary>How long after a dismissal another one is ignored.</summary>
    /// <remarks>
    /// Long enough to cover the panel's close animation and the foreground moving off it, short
    /// enough that a user deliberately opening and closing a panel twice is never swallowed.
    /// </remarks>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(600);

    private DateTime _lastDismissal = DateTime.MinValue;

    /// <summary>
    /// Closes Windows' flyout, if one we recognise is in front.
    /// </summary>
    /// <returns>True when a surface was closed.</returns>
    public bool Dismiss()
    {
        // Two dismissals in quick succession would be worse than none. Closing is asynchronous —
        // it is a synthesised Win+key and the panel animates away — so a second call arriving
        // before the foreground has moved still sees the surface, sends the toggle again, and
        // *reopens* what the first one closed. That is reachable now that a press on the bar
        // dismisses as well as the handler the press then runs: show-desktop and the window
        // picker both call this themselves.
        if (DateTime.UtcNow - _lastDismissal < Cooldown) return false;

        (string? className, string? processName) = Foreground();
        if (className is null) return false;

        switch (SurfaceFor(className, processName))
        {
            case ShellSurface.QuickSettings:
                _lastDismissal = DateTime.UtcNow;
                ManagedShell.Common.Helpers.ShellHelper.ShowActionCenter();
                BringUpLog.Write($"shell surface dismissed: quick settings ({className})");
                return true;

            case ShellSurface.NotificationCentre:
                _lastDismissal = DateTime.UtcNow;
                ManagedShell.Common.Helpers.ShellHelper.ShowNotificationCenter();
                BringUpLog.Write($"shell surface dismissed: notification centre ({className} / {processName})");
                return true;

            default:
                LogUnknownOnce($"{className} / {processName}");
                return false;
        }
    }

    /// <summary>
    /// Which surface a foreground window belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure, so the table can be asserted without a desktop in front of it.
    /// </para>
    /// <para>
    /// <b>The notification centre has no class of its own, and that is why it used to be missed.</b>
    /// It was listed here as <c>NotificationCenterWindow</c>, which nothing on Windows 11 25H2
    /// answers to. Measured, it is a plain <c>Windows.UI.Core.CoreWindow</c> — the generic UWP
    /// class, shared with Settings, the input host and every XAML island on the desktop — hosted
    /// by <c>ShellExperienceHost</c>. Quick Settings keeps a class of its own,
    /// <c>ControlCenterWindow</c>, and has moved host between builds, so it is matched on class
    /// alone exactly as before.
    /// </para>
    /// <para>
    /// <b>So the ambiguous one, and only the ambiguous one, is qualified by its host process.</b>
    /// Matching a bare <c>CoreWindow</c> would be actively dangerous rather than merely wrong:
    /// every dismissal here is a toggle, so firing it at the Settings app — also a
    /// <c>CoreWindow</c> — would <i>open</i> the notification centre on top of whatever the user
    /// was doing. The title would be the other discriminator and is worse, because it is
    /// localised.
    /// </para>
    /// </remarks>
    public static ShellSurface SurfaceFor(string className, string? processName = null) =>
        (className, processName) switch
        {
            ("ControlCenterWindow", _) => ShellSurface.QuickSettings,

            // Kept for older builds, which did have a class of their own.
            ("NotificationCenterWindow", _) => ShellSurface.NotificationCentre,

            ("Windows.UI.Core.CoreWindow", "ShellExperienceHost") => ShellSurface.NotificationCentre,

            _ => ShellSurface.None,
        };

    /// <summary>
    /// Records a foreground class we were asked to close and did not know, once per class.
    /// </summary>
    /// <remarks>
    /// Deduplicated because this runs on every panel the user opens, and most of the time the
    /// answer is some ordinary application window that has nothing to do with the shell. What
    /// makes the line worth writing is the one time it names a flyout under a class this table
    /// does not carry.
    /// </remarks>
    private void LogUnknownOnce(string className)
    {
        if (!_loggedUnknownClasses.Add(className)) return;

        BringUpLog.Write($"foreground window class not a known shell surface: {className}");
    }

    /// <summary>The foreground window's class and the process hosting it.</summary>
    /// <remarks>
    /// The process is read because one of the two surfaces has no distinctive class; see
    /// <see cref="SurfaceFor"/>. It is looked up rather than cached because the hosts change
    /// between builds, and a wrong answer here sends a toggle at the wrong window.
    /// </remarks>
    private static (string? ClassName, string? ProcessName) Foreground() =>
        Describe(NativeMethods.GetForegroundWindow());

    /// <summary>Which of Windows' surfaces a top-level window is, if any.</summary>
    public static ShellSurface SurfaceOf(IntPtr window)
    {
        (string? className, string? processName) = Describe(window);
        return className is null ? ShellSurface.None : SurfaceFor(className, processName);
    }

    private static (string? ClassName, string? ProcessName) Describe(IntPtr window)
    {
        if (window == IntPtr.Zero) return (null, null);

        StringBuilder name = new(256);
        int length = NativeMethods.GetClassName(window, name, name.Capacity);
        if (length <= 0) return (null, null);

        return (name.ToString(), ProcessNameOf(window));
    }

    private static string? ProcessNameOf(IntPtr window)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0) return null;

            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // The window's process has gone between the two calls, which is ordinary for a flyout.
            return null;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
