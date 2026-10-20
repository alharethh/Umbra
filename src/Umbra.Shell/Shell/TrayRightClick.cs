using System.Runtime.InteropServices;

namespace Umbra.Shell.Shell;

/// <summary>
/// The menu request ManagedShell does not send for Explorer's own system icons.
/// </summary>
/// <remarks>
/// <para>
/// A right click on a notification icon is forwarded as <c>WM_RBUTTONUP</c>, followed by
/// <c>WM_CONTEXTMENU</c> only when the icon has declared version 3 or later — a version-0 owner
/// expects neither the message nor its packing. Explorer's battery icon is owned by its
/// <c>SystemTray_Main</c> window and reaches us as <b>version 0</b>, so it was sent only the
/// button-up, which it ignores. Measured on the 150% laptop, Windows 11 25H2, by sending the
/// owner each form directly: <c>WM_RBUTTONUP</c> in either packing raises nothing;
/// <c>WM_CONTEXTMENU</c> raises Windows' own power menu every time — at the screen corner in the
/// version-0 packing, because the owner read the uid out of <c>wParam</c> as an x coordinate, and
/// beside the icon in the version-4 packing. So the owner speaks version 4 whatever version the
/// icon reports, and this sends the one message it answers, in the packing it reads.
/// </para>
/// <para>
/// <b>Keyed on the owner's window class, not on the battery's GUID</b>, because what is being
/// relied on is how that window reads its messages, and every icon it hosts reads them the same
/// way. It is not widened to every version-0 icon: an ordinary application that never asked for
/// version 4 would receive a message it did not ask for, packed in a layout it does not expect.
/// Icons that declared version 3 or 4 are left alone, because ManagedShell has already sent them
/// this exact message and a second would raise the menu twice.
/// </para>
/// <para>
/// <b>The volume icon is not reached by this, and cannot be.</b> It is owned by an ATL window in
/// Explorer that registered <i>no callback message at all</i> (<c>uCallbackMessage</c> = 0), so
/// there is nothing to forward a click to — ManagedShell's left-click override opens Quick
/// Settings for it instead, which is why the left click works. Its right click gets a menu of
/// Umbra's own instead: see <see cref="SpeakerMenu"/>.
/// </para>
/// </remarks>
internal static class TrayRightClick
{
    /// <summary>The class of Explorer's system-icon host window. Measured as the battery's owner.</summary>
    public const string ExplorerSystemTrayClass = "SystemTray_Main";

    private const uint WM_CONTEXTMENU = 0x007B;

    /// <summary>Whether a right click needs a <c>WM_CONTEXTMENU</c> sent by us after ManagedShell's forward.</summary>
    public static bool NeedsContextMenu(uint version, string? ownerClass, uint callbackMessage) =>
        version < 3
        && callbackMessage != 0
        && string.Equals(ownerClass, ExplorerSystemTrayClass, StringComparison.Ordinal);

    /// <summary>
    /// The version-4 packing: the anchor point in <c>wParam</c>, the event in the low word of
    /// <c>lParam</c> and the icon's uid in the high word.
    /// </summary>
    public static (nint WParam, nint LParam) ContextMenuMessage(uint packedCursor, uint uid) =>
        ((nint)packedCursor, (nint)(WM_CONTEXTMENU | ((uid & 0xFFFF) << 16)));

    /// <summary>Sends it, without waiting on Explorer — the same reason ManagedShell uses <c>SendNotifyMessage</c>.</summary>
    public static bool Send(IntPtr owner, uint callbackMessage, uint packedCursor, uint uid)
    {
        (nint wParam, nint lParam) = ContextMenuMessage(packedCursor, uid);
        return SendNotifyMessage(owner, callbackMessage, wParam, lParam);
    }

    /// <summary>The owner window's class name, or empty when the window has gone.</summary>
    public static string ClassOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return "";
        var name = new System.Text.StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
}
