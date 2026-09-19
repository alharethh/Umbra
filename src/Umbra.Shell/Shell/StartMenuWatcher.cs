using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Umbra.Shell.Shell;

/// <summary>
/// Tracks whether the Windows Start menu is currently showing.
/// </summary>
/// <remarks>
/// <para>
/// The menu is <c>StartMenuExperienceHost.exe</c>, a separate process (§7.0), and it takes the
/// foreground while it is open. So "is the foreground window owned by that process" is the whole
/// test — no window-class matching, which would also catch unrelated UWP surfaces.
/// </para>
/// <para>
/// The process identifier is cached and only re-resolved occasionally. Looking a process up by
/// name is far too expensive to do on every poll, and the host is long-lived — it starts with the
/// session and stays for it.
/// </para>
/// </remarks>
internal sealed class StartMenuWatcher
{
    /// <summary>
    /// Processes that can host the open Start surface.
    /// </summary>
    /// <remarks>
    /// <b>Both are needed, and which one answers is not obvious.</b> §7.0 names
    /// <c>StartMenuExperienceHost</c>, and that process does exist and does own the menu — but on
    /// Windows 11 25H2, opening Start puts <c>SearchHost</c> in the foreground, because Start and
    /// search are one surface. Watching only the documented process detects the menu as closed the
    /// entire time it is open, which is exactly what the first version of this did.
    /// </remarks>
    private static readonly string[] HostProcesses = ["StartMenuExperienceHost", "SearchHost"];

    private static readonly TimeSpan ResolveInterval = TimeSpan.FromSeconds(30);

    private readonly HashSet<int> _hostProcessIds = [];
    private DateTime _resolvedAt = DateTime.MinValue;

    /// <summary>Whether the Start menu is currently in front.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Re-reads the current state. Cheap enough to call from an existing timer.</summary>
    /// <returns>True when the state changed.</returns>
    public bool Poll()
    {
        bool open = IsHostForeground();
        if (open == IsOpen) return false;

        IsOpen = open;
        return true;
    }

    /// <summary>Closes the Start menu, if it is open.</summary>
    /// <returns>True when there was a menu to close.</returns>
    /// <remarks>
    /// <para>
    /// Windows closes Start by itself when you click anywhere outside it. It does not do that for
    /// us: the bar carries <c>WS_EX_NOACTIVATE</c>, so clicking it never moves the foreground and
    /// Start is never told it lost anything. Anything the bar then opens is drawn underneath.
    /// </para>
    /// <para>
    /// <b>Out-ranking it is not an option.</b> The Start menu sits in a Z-order band above ordinary
    /// topmost windows, and entering that band needs <c>SetWindowBand</c> and a UIAccess manifest —
    /// which means a signed binary installed to a trusted location. Measured: a context menu raised
    /// with <c>SetWindowPos(HWND_TOPMOST)</c> reports success and still draws behind. So the fix is
    /// to do what the click should have done.
    /// </para>
    /// <para>
    /// <c>ShowStartMenu</c> is a toggle, which makes it dangerous to call blind — it would
    /// <i>open</i> the menu we are trying to be rid of. <see cref="Poll"/> is what makes it safe,
    /// and it re-reads rather than trusting a cached value for exactly that reason.
    /// </para>
    /// </remarks>
    public bool Dismiss()
    {
        Poll();
        if (!IsOpen) return false;

        ManagedShell.Common.Helpers.ShellHelper.ShowStartMenu();
        IsOpen = false;

        return true;
    }

    private bool IsHostForeground()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(foreground, out int processId);
        if (processId == 0) return false;

        // Fast path: the cached set already knows this process, so an open menu costs one lookup.
        if (_hostProcessIds.Contains(processId)) return true;

        RefreshHostProcessIds();
        return _hostProcessIds.Contains(processId);
    }

    /// <summary>
    /// Re-reads the host process identifiers, at most once per <see cref="ResolveInterval"/>.
    /// Looking processes up by name is far too expensive to do on every poll.
    /// </summary>
    private void RefreshHostProcessIds()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _resolvedAt < ResolveInterval) return;

        _resolvedAt = now;
        _hostProcessIds.Clear();

        foreach (string name in HostProcesses)
        {
            try
            {
                foreach (Process host in Process.GetProcessesByName(name))
                {
                    _hostProcessIds.Add(host.Id);
                    host.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
                // The process went away between enumeration and inspection; nothing to record.
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    }
}
