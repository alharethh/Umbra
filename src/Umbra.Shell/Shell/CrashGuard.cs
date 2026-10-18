using System.Diagnostics;
using System.Globalization;
using System.IO;
using ManagedShell.AppBar;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// A second, idle process that puts Explorer's taskbar back when the bar dies without doing it
/// itself: <c>Umbra.exe --guard &lt;pid&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExplorerStateGuard"/> already survives a hard kill, but only as far as the
/// <i>next launch</i>: the marker file waits on disk and nothing reads it until Umbra starts
/// again. Measured on the second machine (QA 2026-10-16, cases 0.2 and 0.9): after
/// <c>Stop-Process -Force</c> Explorer's taskbar stayed in runtime autohide — two pixels on
/// screen, the work area unreserved — for as long as the user did not start Umbra again, which
/// for someone who has just watched it crash is plausibly forever. No in-process handler can
/// cover a kill, a native crash, <c>FailFast</c>, a stack overflow or "close the program" on a
/// hung window, because every one of them ends the process without running managed code.
/// </para>
/// <para>
/// So the bar starts this once its marker file exists, and it does nothing but wait on the bar's
/// process handle. When the bar is gone it runs the same recovery the next launch would have
/// run — <see cref="ExplorerStateGuard.Capture"/> then <see cref="ExplorerStateGuard.Restore"/>
/// against the persisted state — and exits. After a clean exit the marker is already gone and it
/// exits having touched nothing.
/// </para>
/// <para>
/// <b>It claims the single-instance lock before it reads the marker</b>, with a zero timeout, and
/// gives up if it cannot have it. Whoever holds that lock is a bar, and a running bar's marker is
/// its own record of the user's taskbar, not leftovers; restoring from it would hand Explorer's
/// taskbar back underneath a working bar. That is also why the order cannot be reversed: a
/// replacement could claim the lock and write its own marker between our check and our restore.
/// It holds the lock for as long as one <c>ABM_SETSTATE</c> and a registry write take, which is
/// why an ordinary launch, which waits for nothing, still finds it free in practice.
/// </para>
/// <para>
/// <b>It never suppresses anything.</b> It constructs no <c>ShellManager</c>, no tray, no
/// AppBar and no WPF <c>Application</c> — only an <see cref="ExplorerHelper"/> to send
/// <c>ABM_SETSTATE</c> through, and only when there is a marker to act on.
/// </para>
/// <para>
/// <b>Known gap, accepted:</b> <c>Stop-Process -Name Umbra</c> (or ending the whole process tree)
/// kills the guard too, since it is also <c>Umbra.exe</c>. That case still falls back to the next
/// launch. Covering it would need something outside the process name, such as a scheduled task,
/// which is a heavier thing to install on a stranger's machine than the failure warrants.
/// </para>
/// </remarks>
internal static class CrashGuard
{
    /// <summary>The flag that turns a launch into a guard.</summary>
    public const string Flag = "--guard";

    /// <summary>What the guard found once the bar was gone.</summary>
    public enum Outcome
    {
        /// <summary>No marker: the bar exited cleanly and restored Explorer itself.</summary>
        NothingToDo,

        /// <summary>A marker was left behind, and Explorer's taskbar has been put back.</summary>
        Restored,

        /// <summary>Another bar holds the lock, so the marker is its own and is left alone.</summary>
        AnotherBarIsRunning,
    }

    /// <summary>True when the command line asks for a guard rather than a bar.</summary>
    public static bool IsGuardLaunch(string[] args) =>
        args.Length >= 1 && string.Equals(args[0], Flag, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts a guard for this process. Called by the bar once <see cref="ShellHost"/> has
    /// written the marker. Best-effort: a bar without a guard is what shipped before.
    /// </summary>
    public static void Launch()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            BringUpLog.Write("crash guard not started: the process path is unknown");
            return;
        }

        try
        {
            using Process? guard = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                Arguments = string.Create(CultureInfo.InvariantCulture, $"{Flag} {Environment.ProcessId}"),
            });

            BringUpLog.Write($"crash guard started, pid {guard?.Id}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            BringUpLog.Write(e, "crash guard could not be started");
        }
    }

    /// <summary>The guard process's whole life. Returns its exit code.</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2 ||
            !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int barId))
        {
            BringUpLog.Write("crash guard: no bar process id given; exiting");
            return 2;
        }

        WaitForBar(barId);

        Outcome outcome = RecoverIfOrphaned(
            lockName: null,
            statePath: null,
            taskbar: () => new OrphanedTaskbarState(new ExplorerTaskbarState(new ExplorerHelper())));

        BringUpLog.Write($"crash guard: bar {barId} has gone; {Describe(outcome)}");
        return 0;
    }

    /// <summary>
    /// The decision the guard makes once the bar is gone, apart from the waiting so it can be
    /// tested without a process to kill.
    /// </summary>
    /// <param name="lockName">Single-instance lock; defaults to <see cref="SingleInstance.DefaultName"/>.</param>
    /// <param name="statePath">Marker file; defaults to <see cref="ExplorerStateGuard.DefaultStatePath"/>.</param>
    /// <param name="taskbar">
    /// Made only once there is something to restore, so a clean exit never reaches Explorer at all.
    /// </param>
    /// <param name="lockTimeout">
    /// Zero in the guard: by the time a process handle is signalled every thread in it has exited,
    /// so a dead bar's lock is already abandoned, and anyone who holds it is a bar, which waiting
    /// would only mean waiting for until it exits and restores Explorer itself. Tests pass a grace,
    /// because a thread standing in for the bar has no such guarantee after <c>Join</c>.
    /// </param>
    public static Outcome RecoverIfOrphaned(
        string? lockName, string? statePath, Func<ITaskbarState> taskbar, TimeSpan lockTimeout = default)
    {
        ArgumentNullException.ThrowIfNull(taskbar);

        using SingleInstance? held = SingleInstance.Claim(lockName, lockTimeout);
        if (held is null) return Outcome.AnotherBarIsRunning;

        string path = statePath ?? ExplorerStateGuard.DefaultStatePath;

        // Checked here rather than left to Capture, which on finding no marker would read the
        // current state and write a new one — leaving a marker behind for a bar that is not
        // running, which the next launch would then "recover" from.
        if (!File.Exists(path)) return Outcome.NothingToDo;

        // The next launch's recovery, verbatim: Capture puts the persisted state back and
        // records it, Restore applies it again and deletes the marker.
        ExplorerStateGuard guard = new(taskbar(), path);
        guard.Capture();
        guard.Restore();

        return Outcome.Restored;
    }

    /// <summary>Blocks until the bar process has exited, or returns at once if it already has.</summary>
    private static void WaitForBar(int barId)
    {
        try
        {
            using Process bar = Process.GetProcessById(barId);

            // A process id can be reused. The bar starts us immediately, so this is unlikely, but
            // waiting on a stranger would leave the bar unguarded with nothing to show for it.
            if (!string.Equals(bar.ProcessName, Process.GetCurrentProcess().ProcessName,
                               StringComparison.OrdinalIgnoreCase))
            {
                BringUpLog.Write($"crash guard: process {barId} is not Umbra; treating the bar as gone");
                return;
            }

            BringUpLog.Write($"crash guard: watching bar {barId}");
            bar.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Gone before we looked — a bar that died during startup. The lock and the marker
            // still decide what happens next.
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Explorer's taskbar as a process that did not suppress it has to see it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Restoring the AppBar state alone left the taskbar invisible.</b> Measured on the first
    /// live run: after the kill the guard put Explorer back to <c>ABM_GETSTATE=0</c> and the work
    /// area to its 72 px reservation — and <c>Shell_TrayWnd</c> stayed hidden, so the user had a
    /// black strip reserved for a taskbar that was not drawn. ManagedShell suppresses with two
    /// things, autohide <i>and</i> <c>SWP_HIDEWINDOW</c>, and only its own <c>ExplorerHelper</c>
    /// instance knows to undo the second: <c>HideExplorerTaskbar = false</c> on a fresh one is a
    /// no-op, because it was never true there.
    /// </para>
    /// <para>
    /// So hiding is undone here by hand, with <c>SW_SHOWNA</c>: shown without activating and
    /// without touching its Z-order, which Explorer manages itself. The next launch's recovery
    /// does not need this, because it suppresses the taskbar again straight away.
    /// </para>
    /// </remarks>
    private sealed class OrphanedTaskbarState(ITaskbarState inner) : ITaskbarState
    {
        private const int SwShowNa = 8;

        public ManagedShell.Interop.NativeMethods.ABState GetState() => inner.GetState();

        public void SetState(ManagedShell.Interop.NativeMethods.ABState state) => inner.SetState(state);

        public int? GetAlignment() => inner.GetAlignment();

        public void SetAlignment(int? alignment) => inner.SetAlignment(alignment);

        public bool Hidden
        {
            get => false;
            set
            {
                if (value) return;

                // The bar is dead, so every taskbar window left is Explorer's own.
                ShowAll("Shell_TrayWnd");
                ShowAll("Shell_SecondaryTrayWnd");
            }
        }

        private static void ShowAll(string className)
        {
            IntPtr window = IntPtr.Zero;
            while ((window = FindWindowEx(IntPtr.Zero, window, className, null)) != IntPtr.Zero)
                ShowWindow(window, SwShowNa);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);
    }

    private static string Describe(Outcome outcome) => outcome switch
    {
        Outcome.Restored => "it left Explorer's taskbar suppressed, and it has been restored",
        Outcome.AnotherBarIsRunning => "another bar holds the lock, so nothing was touched",
        _ => "it exited cleanly, so there was nothing to do",
    };
}
