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
/// <b>Third-party tray menus need nothing from us.</b> An application showing its menu through
/// <c>TrackPopupMenu</c> holds a global mouse capture for as long as the menu is up, so the click
/// that would have opened our panel is delivered to the menu, which dismisses itself. The Windows
/// taskbar behaves the same way — the first click closes the menu and the second opens the panel.
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

    /// <summary>
    /// Closes Windows' flyout, if one we recognise is in front.
    /// </summary>
    /// <returns>True when a surface was closed.</returns>
    public bool Dismiss()
    {
        string? className = ForegroundClassName();
        if (className is null) return false;

        switch (SurfaceFor(className))
        {
            case ShellSurface.QuickSettings:
                ManagedShell.Common.Helpers.ShellHelper.ShowActionCenter();
                BringUpLog.Write($"shell surface dismissed: quick settings ({className})");
                return true;

            case ShellSurface.NotificationCentre:
                ManagedShell.Common.Helpers.ShellHelper.ShowNotificationCenter();
                BringUpLog.Write($"shell surface dismissed: notification centre ({className})");
                return true;

            default:
                LogUnknownOnce(className);
                return false;
        }
    }

    /// <summary>
    /// Which surface a window class belongs to.
    /// </summary>
    /// <remarks>
    /// Pure, so the table can be asserted without a desktop in front of it. Matching is on the
    /// class rather than on the owning process because the hosts have moved — Quick Settings was
    /// <c>ShellExperienceHost</c> and is <c>ShellHost</c> on 25H2 — while the class has not.
    /// </remarks>
    public static ShellSurface SurfaceFor(string className) => className switch
    {
        "ControlCenterWindow" => ShellSurface.QuickSettings,
        "NotificationCenterWindow" => ShellSurface.NotificationCentre,
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

    private static string? ForegroundClassName()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;

        StringBuilder name = new(256);
        int length = NativeMethods.GetClassName(foreground, name, name.Capacity);

        return length > 0 ? name.ToString() : null;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    }
}
