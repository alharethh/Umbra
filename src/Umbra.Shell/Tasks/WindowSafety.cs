using System.Runtime.InteropServices;
using System.Text;
using ManagedShell.WindowsTasks;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Tasks;

/// <summary>Whether a window may be closed, and why not when it may not.</summary>
/// <param name="Allowed">True when closing is safe.</param>
/// <param name="Reason">Human-readable, and written to the log on every refusal.</param>
internal readonly record struct CloseVerdict(bool Allowed, string Reason)
{
    public static CloseVerdict Yes { get; } = new(true, "ordinary application window");

    public static CloseVerdict No(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether a window is safe to close, and closes it without ever blocking on its process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Closing File Explorer from the bar took the whole desktop down with it.
/// The close path itself was read out of ManagedShell rather than guessed at, and it does not
/// terminate anything: <c>ApplicationWindow.DoClose</c> calls
/// <c>SetForegroundWindow(GetLastActivePopup(hwnd))</c> and then
/// <c>SendMessageTimeout(hwnd, WM_SYSCOMMAND, SC_CLOSE, 0, SMTO_ABORTIFHUNG, 200 ms)</c>, and
/// <c>TasksService.CloseWindow</c> disposes the window and drops it from the task list if that does
/// not answer in time. No process is killed anywhere in it.
/// </para>
/// <para>
/// <b>The mechanism is therefore not established, and this class does not pretend otherwise.</b>
/// A live inventory taken while the bar was running showed the <c>explorer.exe</c> slot holding
/// exactly one <c>CabinetWClass</c> window — an ordinary browser window, not the desktop — so the
/// obvious theory, that the bar was sending a close to a shell window, is disproved for at least
/// that moment. What this class does is remove every part of the path that could plausibly be
/// responsible, and record what was targeted so the next occurrence leaves evidence:
/// </para>
/// <list type="bullet">
/// <item><b>Posted, never sent.</b> A synchronous cross-process message from a shell replacement
/// into the shell is a deadlock waiting to happen, and there is nothing useful to do with the
/// reply. <c>PostMessage(WM_CLOSE)</c> returns immediately.</item>
/// <item><b>No foreground steal.</b> Nothing needs the window activated in order to close it, and
/// <c>SetForegroundWindow</c> against the shell process is one more interaction that does not have
/// to happen.</item>
/// <item><b>Never escalates.</b> A window that ignores <c>WM_CLOSE</c> stays open, which is what
/// every other taskbar does. Nothing here terminates a process, ever.</item>
/// <item><b>Never disposes a slow window.</b> ManagedShell drops a window from the task list when
/// the close does not answer within 200 ms. A slow application is not a closed one, and dropping it
/// makes the bar disagree with the desktop until the next rebuild.</item>
/// <item><b>The shell process gets a whitelist, not a blacklist.</b> See below.</item>
/// </list>
/// <para>
/// <b>Why a whitelist for the shell process.</b> <c>explorer.exe</c> owns the desktop, the taskbar
/// and every File Explorer window, and they all collapse into one slot under
/// <see cref="AppIdentity"/>. For every other process, being wrong about a window closes something
/// the user did not want closed; for this one it can end the session. So the rule is inverted
/// there: a window belonging to the process that owns the desktop is closed only if it is a known
/// browser class, and refused otherwise. Membership is decided by comparing process ids against
/// <c>GetShellWindow</c>'s owner, which means File Explorer running in its own process — the
/// "launch folder windows in a separate process" option — is correctly treated as an ordinary
/// application.
/// </para>
/// </remarks>
internal static class WindowSafety
{
    /// <summary>Window classes that belong to the shell itself and are never closed.</summary>
    private static readonly string[] ShellClasses =
    [
        "Progman",
        "WorkerW",
        "SHELLDLL_DefView",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "TrayNotifyWnd",
        "ReBarWindow32",
        "MSTaskSwWClass",
        "MSTaskListWClass",
        "NotifyIconOverflowWindow",
        "ApplicationManager_DesktopShellWindow",

        // Deliberately NOT listed: Windows.UI.Core.CoreWindow. That is the class every ordinary
        // UWP application window uses, and blocking it would make them unclosable from the bar.
    ];

    /// <summary>The only shell-process windows that are ordinary documents.</summary>
    /// <remarks>
    /// <c>CabinetWClass</c> is a File Explorer window; <c>ExploreWClass</c> is the older
    /// explorer-bar variant that still turns up. Anything else owned by the shell process is
    /// infrastructure as far as this code is concerned.
    /// </remarks>
    private static readonly string[] ExplorerBrowserClasses =
    [
        "CabinetWClass",
        "ExploreWClass",
    ];

    /// <summary>
    /// The whole decision, as a pure function of the facts, so it can be tested without a desktop.
    /// </summary>
    public static CloseVerdict Evaluate(string className, bool isDesktopWindow, bool belongsToShellProcess)
    {
        if (isDesktopWindow) return CloseVerdict.No("it is the desktop");

        foreach (string shell in ShellClasses)
            if (string.Equals(className, shell, StringComparison.OrdinalIgnoreCase))
                return CloseVerdict.No($"'{className}' is a shell window");

        if (!belongsToShellProcess) return CloseVerdict.Yes;

        foreach (string browser in ExplorerBrowserClasses)
            if (string.Equals(className, browser, StringComparison.OrdinalIgnoreCase))
                return CloseVerdict.Yes;

        // Owned by the process that draws the desktop, and not a browser window. Being wrong here
        // ends the session, so the answer is no.
        return CloseVerdict.No($"'{className}' belongs to the shell process and is not a browser window");
    }

    /// <summary>Gathers the facts about a live window and evaluates them.</summary>
    public static CloseVerdict Decide(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return CloseVerdict.No("not a window");

        IntPtr shellWindow = GetShellWindow();
        bool isDesktop = handle == shellWindow || handle == GetDesktopWindow();

        return Evaluate(ClassOf(handle), isDesktop, BelongsToShellProcess(handle, shellWindow));
    }

    /// <summary>Whether closing this window is permitted.</summary>
    public static bool CanClose(IntPtr handle) => Decide(handle).Allowed;

    /// <summary>
    /// Asks a window to close. Returns false when it was refused.
    /// </summary>
    /// <remarks>
    /// The target is logged <i>before</i> the message goes out, and <see cref="BringUpLog"/> opens
    /// and closes the file on every write, so the line is on disk even if the shell does not
    /// survive what happens next. If the File Explorer crash recurs, that line names the window.
    /// </remarks>
    public static bool TryClose(ApplicationWindow? window)
    {
        if (window is null) return false;

        IntPtr handle = window.Handle;
        CloseVerdict verdict = Decide(handle);

        if (!verdict.Allowed)
        {
            BringUpLog.Write($"close REFUSED {Describe(handle)} — {verdict.Reason}");
            return false;
        }

        BringUpLog.Write($"closing {Describe(handle)}");

        // Posted, never sent, and with no preceding activation. WM_CLOSE is what a window's own
        // close button ends up delivering, so applications still get their chance to prompt.
        PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    /// <summary>Handle, class and title, for the log.</summary>
    public static string Describe(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "(null window)";

        var title = new StringBuilder(256);
        GetWindowText(handle, title, title.Capacity);

        return $"0x{handle.ToInt64():X} [{ClassOf(handle)}] '{title}'";
    }

    /// <summary>
    /// Whether this window is owned by the process that owns the desktop.
    /// </summary>
    /// <remarks>
    /// Compared by process id rather than by executable name: it needs no process handle, cannot be
    /// defeated by a copy of explorer.exe somewhere else, and correctly says "no" when File
    /// Explorer has been configured to run in its own process.
    /// </remarks>
    private static bool BelongsToShellProcess(IntPtr handle, IntPtr shellWindow)
    {
        if (shellWindow == IntPtr.Zero) return false;

        GetWindowThreadProcessId(shellWindow, out uint shellProcess);
        GetWindowThreadProcessId(handle, out uint windowProcess);

        return shellProcess != 0 && shellProcess == windowProcess;
    }

    private static string ClassOf(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private const uint WmClose = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder buffer, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder buffer, int capacity);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
