using ManagedShell.WindowsTray;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// The menu Umbra draws for the speaker icon, because Windows will not draw one for us.
/// </summary>
/// <remarks>
/// <para>
/// The volume icon's owner is an ATL window in Explorer that registered <b>no callback message</b>
/// (<c>uCallbackMessage</c> = 0), so a forwarded right click is <c>WM_NULL</c> and nothing happens
/// — see <see cref="TrayRightClick"/>. Windows' own taskbar draws the speaker menu itself, from
/// <c>SndVolSSO.dll</c>; there is no message that asks it to. So the bar draws its own, in the
/// dark menu theme, with the entries Windows' menu has.
/// </para>
/// <para>
/// <b>The targets were read out of <c>SndVolSSO.dll</c></b> on Windows 11 25H2 rather than taken
/// from a guide. The troubleshooter in particular has moved: the <c>msdt</c> diagnostics are
/// retired, and Windows' own speaker menu now hands off to Get Help with the
/// <c>windows-speaker-icon</c> invoker below. The volume mixer is the Settings page Windows 11's
/// "Open volume mixer" opens, not the legacy <c>SndVol.exe</c>.
/// </para>
/// <para>
/// <b>Only while the icon has no callback.</b> If a later Windows gives the owner a callback
/// message, the forwarded click reaches it and Windows' own menu appears; drawing ours as well
/// would put two menus on screen.
/// </para>
/// </remarks>
internal static class SpeakerMenu
{
    /// <summary>One item: what the menu says and what it opens.</summary>
    public readonly record struct Entry(string Label, string Target);

    /// <summary>Windows' entries, in Windows' order: the troubleshooter furthest from the bar.</summary>
    public static IReadOnlyList<Entry> Entries { get; } =
    [
        new("Troubleshoot sound problems",
            "ms-contact-support://windows-speaker-icon//?Product=Windows&SearchKey=Audio%20Issues"),
        new("Open volume mixer", "ms-settings:apps-volume"),
        new("Sound settings", "ms-settings:sound"),
    ];

    /// <summary>Whether a right click on this icon gets our menu instead of being forwarded.</summary>
    public static bool Replaces(Guid guid, uint callbackMessage) =>
        callbackMessage == 0
        && string.Equals(guid.ToString(), NotificationArea.VOLUME_GUID, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a press on this icon should borrow the foreground to grant it to the icon's owner.
    /// False only for the right press that opens our own menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grant (<see cref="WindowOrder.AllowForeground"/>) exists so an owner can put its menu
    /// in front, and here there is no owner menu: nothing is forwarded. <b>The borrow is not
    /// free.</b> Measured 2026-10-23 with Windows Terminal in front: the foreground was Terminal
    /// just before the <c>AttachThreadInput</c> pair and the bar's own <c>WS_EX_NOACTIVATE</c>
    /// window just after, and it stayed there after the menu closed, so typed keys went nowhere
    /// (backlog 3p). With Notepad in front the same press left Notepad alone, which is why the
    /// repro was 4 in 5 and not every time. An owner that raises a menu takes the foreground
    /// straight back and hides this; our menu takes nothing, so it showed.
    /// </para>
    /// <para>
    /// A left press still borrows: ManagedShell answers it for the volume icon by opening Quick
    /// Settings, and that is not ours to change here.
    /// </para>
    /// </remarks>
    public static bool GrantsOwnerForeground(bool rightButton, Guid guid, uint callbackMessage) =>
        !(rightButton && Replaces(guid, callbackMessage));

    /// <summary>Opens an entry's target through the shell, which owns every one of these schemes.</summary>
    public static void Open(Entry entry)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(entry.Target) { UseShellExecute = true });
            BringUpLog.Write($"speaker menu: {entry.Label} -> {entry.Target}");
        }
        catch (Exception e)
        {
            // Logged, not shown: an item that silently does nothing is the failure to rule out.
            BringUpLog.Write(e, $"speaker menu: could not open {entry.Target}");
        }
    }
}
