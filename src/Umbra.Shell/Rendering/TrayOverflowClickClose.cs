using System;
using System.Windows.Input;

namespace Umbra.Shell.Rendering;

/// <summary>
/// When a click on a notification icon closes the overflow flyout.
/// </summary>
/// <remarks>
/// <para>
/// <b>A left click on an icon inside the flyout does not close it at once.</b> It used to, on the
/// button-up, and a double-click then never reached the application: the second press landed on
/// whatever was underneath, because the icon it was aimed at had gone with the panel. ManagedShell
/// turns a second press into <c>WM_LBUTTONDBLCLK</c> only if it arrives on the same icon within
/// the double-click time of the first button-up, so the flyout has to still be there for that long.
/// </para>
/// <para>
/// So the first click's close waits out the double-click time, and the press that completes a
/// double-click closes it as soon as its button comes up. A single click therefore leaves the
/// panel up for one double-click interval (500 ms by default) over whatever the application
/// raised, then closes it — the price of not knowing, at the first button-up, whether a second
/// press is coming. Every taskbar pays it somewhere; Windows' own overflow pays it by staying up
/// until something else takes the foreground.
/// </para>
/// <para>
/// An icon on the bar itself is not detached by closing the flyout, so a left click there closes
/// it at once, as before. A right click never closes it: the application's menu appears beside the
/// icon and would be left floating next to nothing.
/// </para>
/// </remarks>
internal static class TrayOverflowClickClose
{
    /// <summary>
    /// How long after this button-up the flyout should close, or <see langword="null"/> to leave
    /// it open.
    /// </summary>
    /// <param name="button">The button that came up.</param>
    /// <param name="iconIsInFlyout">Whether the icon clicked is inside the flyout.</param>
    /// <param name="completesDoubleClick">
    /// Whether the press for this click came within the double-click time of the previous left
    /// button-up on the same icon — see <see cref="CompletesDoubleClick"/>.
    /// </param>
    /// <param name="doubleClickTimeMs">The system double-click time.</param>
    public static TimeSpan? After(
        MouseButton button, bool iconIsInFlyout, bool completesDoubleClick, int doubleClickTimeMs)
    {
        if (button != MouseButton.Left) return null;
        if (!iconIsInFlyout || completesDoubleClick) return TimeSpan.Zero;

        return TimeSpan.FromMilliseconds(Math.Max(0, doubleClickTimeMs));
    }

    /// <summary>
    /// Whether a left press is the second half of a double-click, by the rule ManagedShell itself
    /// applies when it decides to send <c>WM_LBUTTONDBLCLK</c>: the same icon, within the
    /// double-click time of the previous left button-up.
    /// </summary>
    public static bool CompletesDoubleClick(
        object? previousIcon, long previousUpMs, object icon, long pressMs, int doubleClickTimeMs) =>
        previousIcon is not null
        && ReferenceEquals(previousIcon, icon)
        && pressMs - previousUpMs >= 0
        && pressMs - previousUpMs <= doubleClickTimeMs;
}
