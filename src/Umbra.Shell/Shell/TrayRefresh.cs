using System.Runtime.InteropServices;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Asks every application to send its notification icons again.
/// </summary>
/// <remarks>
/// <para>
/// Icons that existed before the bar started reach it only one way: Windows' <c>TaskbarCreated</c>
/// broadcast, to which every well-behaved application answers by adding its icon again.
/// ManagedShell sends it once, at startup, and that one send can go unanswered. Measured from the
/// log on 2026-10-10: two startups received Ascent, OBS, NVIDIA Broadcast, a packaged app and Discord
/// within a second, and the one between them received none of them — its first icon arrived eight
/// seconds late and only applications that happened to update afterwards ever appeared. Reported
/// as the tray showing three icons when nine applications had one.
/// </para>
/// <para>
/// Sending it again is harmless — Explorer does exactly this whenever it restarts, and an
/// application re-adding an icon the bar already holds updates it rather than duplicating it. So
/// the bar sends it once more shortly after starting, and the menu offers it on demand.
/// </para>
/// <para>
/// <c>SendNotifyMessage</c>, not <c>SendMessage</c>: a broadcast through <c>SendMessage</c> waits
/// for every top-level window on the desktop to answer, and one hung application would freeze the
/// bar. Explorer uses the notify form for the same reason.
/// </para>
/// </remarks>
internal static class TrayRefresh
{
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    /// <summary>Broadcasts <c>TaskbarCreated</c>.</summary>
    public static void Broadcast(string reason)
    {
        uint message = RegisterWindowMessage("TaskbarCreated");

        bool sent = message != 0 && SendNotifyMessage(HwndBroadcast, message, IntPtr.Zero, IntPtr.Zero);
        BringUpLog.Write($"tray refresh ({reason}): TaskbarCreated broadcast {(sent ? "sent" : "failed")}");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
