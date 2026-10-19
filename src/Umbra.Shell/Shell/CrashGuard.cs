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
/// <b>It is not the bar's child.</b> The bar starts <c>Umbra.exe --guard-launch &lt;pid&gt;</c>,
/// which starts the guard and exits at once, so the guard's parent is a process that no longer
/// exists. Ending the bar's process tree — <c>taskkill /T</c>, and Task Manager's "End task",
/// which groups an application with its children — follows parent links, and with the link
/// broken it ends the bar alone. Measured on the second machine (2026-10-18): as the bar's child,
/// <c>taskkill /T /F</c> took the guard with it and left Explorer's taskbar auto-hidden and
/// unreserved; detached, the guard survives and puts it back. The one-hop launcher was chosen
/// over <c>PROC_THREAD_ATTRIBUTE_PARENT_PROCESS</c>, which would do the same in one process but
/// is parent-process spoofing, a pattern antivirus heuristics look for, and over starting through
/// <c>explorer.exe</c>, which passes no arguments.
/// </para>
/// <para>
/// <b>Known gap, accepted:</b> <c>Stop-Process -Name Umbra</c> (or <c>taskkill /IM Umbra.exe</c>)
/// kills the guard too, since it is also <c>Umbra.exe</c>. That case still falls back to the next
/// launch. Measured 2026-10-18 with Explorer's saved auto-hide off (<c>StuckRects3</c> byte 8 =
/// <c>0x02</c>): Explorer's taskbar comes back visible but auto-hidden, a 2 px sliver that
/// reveals on hover and reserves no space, until Umbra is started again or Explorer restarts.
/// Earlier, with byte 8 = <c>0x03</c>, it stayed invisible. Covering it would need something
/// outside the process name, such as a scheduled task, which is a heavier thing to install on a
/// stranger's machine than the failure warrants.
/// </para>
/// </remarks>
internal static class CrashGuard
{
    /// <summary>The flag that turns a launch into a guard.</summary>
    public const string Flag = "--guard";

    /// <summary>
    /// The flag for the short-lived process between the bar and its guard, which exists only so
    /// the guard is not the bar's child.
    /// </summary>
    public const string LauncherFlag = "--guard-launch";

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
    /// True when the command line asks for the launcher hop. Exact, like <see cref="IsGuardLaunch"/>:
    /// a prefix match on <c>--guard</c> would take the launcher for a guard, which would then watch
    /// the bar as its child — working, and silently back inside the bar's process tree.
    /// </summary>
    public static bool IsLauncherLaunch(string[] args) =>
        args.Length >= 1 && string.Equals(args[0], LauncherFlag, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The bar's command line for the launcher hop. <see cref="Launch"/> starts exactly this, and
    /// <see cref="Start"/> takes a finished command line rather than a flag, so a test of this is
    /// a test of what the bar actually starts.
    /// </summary>
    /// <remarks>
    /// The flag is the whole point. Starting <see cref="Flag"/> here instead would still work —
    /// the guard would watch the bar and restore after a crash — and would put the guard straight
    /// back inside the bar's process tree, where a tree kill takes it too.
    /// </remarks>
    public static string LauncherArguments(int barId) => Arguments(LauncherFlag, barId);

    /// <summary>
    /// The launcher's command line for the guard, from the launcher's own arguments, or null when
    /// they carry no bar process id. <see cref="RunLauncher"/> starts exactly this.
    /// </summary>
    /// <remarks>
    /// It must name <see cref="Flag"/>, never <see cref="LauncherFlag"/>: a launcher that started
    /// another launcher would exit, and that one would start another, a chain of processes with no
    /// guard at the end of it.
    /// </remarks>
    public static string? GuardArgumentsFor(string[] launcherArgs)
    {
        if (!IsLauncherLaunch(launcherArgs) || launcherArgs.Length < 2 ||
            !int.TryParse(launcherArgs[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int barId))
        {
            return null;
        }

        return Arguments(Flag, barId);
    }

    private static string Arguments(string flag, int barId) =>
        string.Create(CultureInfo.InvariantCulture, $"{flag} {barId}");

    /// <summary>
    /// Starts a guard for this process, through the launcher hop. Called by the bar once
    /// <see cref="ShellHost"/> has written the marker. Best-effort: a bar without a guard is what
    /// shipped before.
    /// </summary>
    /// <remarks>
    /// Not waited for. If the bar dies before the launcher has started the guard, the guard finds
    /// the bar gone, and the lock and the marker decide what happens, exactly as for a bar that
    /// died during its own startup.
    /// </remarks>
    public static void Launch() => Start(LauncherArguments(Environment.ProcessId), "crash guard launcher");

    /// <summary>
    /// The launcher's whole life: start the guard for the bar it was given, and exit, which is
    /// what leaves the guard without a living parent. Returns its exit code.
    /// </summary>
    public static int RunLauncher(string[] args)
    {
        string? guardArgs = GuardArgumentsFor(args);
        if (guardArgs is null)
        {
            BringUpLog.Write("crash guard launcher: no bar process id given; exiting");
            return 2;
        }

        return Start(guardArgs, "crash guard") ? 0 : 1;
    }

    private static bool Start(string arguments, string what)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            BringUpLog.Write($"{what} not started: the process path is unknown");
            return false;
        }

        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                Arguments = arguments,
            });

            BringUpLog.Write($"{what} started as \"{arguments}\", pid {started?.Id}");
            return started is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            BringUpLog.Write(e, $"{what} could not be started");
            return false;
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

        bool ended = WaitForBar(barId);

        Outcome outcome = RecoverIfOrphaned(
            lockName: null,
            statePath: null,
            taskbar: () => new OrphanedTaskbarState(new ExplorerTaskbarState(new ExplorerHelper())));

        BringUpLog.Write($"crash guard: bar {barId} has gone; {Describe(outcome)}");

        // After the restore, never before: if the new bar fails to start, Explorer's taskbar is
        // already back, and if it starts, it captures the user's taskbar rather than ours.
        if (ended) RelaunchAfterHang(outcome);
        return 0;
    }

    /// <summary>
    /// How long after one relaunch the guard will not relaunch again. Ten minutes: long enough
    /// that a bar which hangs on every start is ended twice and then stays down, short enough
    /// that tomorrow's resume from sleep still gets its bar back.
    /// </summary>
    public static readonly TimeSpan RelaunchWindow = TimeSpan.FromMinutes(10);

    /// <summary>Where the last relaunch after a hang is recorded.</summary>
    public static string DefaultRelaunchStampPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Umbra",
        "hang-relaunch");

    /// <summary>
    /// Whether a bar ended for not answering should be started again, given when the last one
    /// was. A clock that has gone backwards allows it: the loop this guards against is bounded by
    /// the restore in between anyway, and refusing would leave the user without their bar.
    /// </summary>
    public static bool ShouldRelaunch(DateTime? lastRelaunchUtc, DateTime nowUtc) =>
        lastRelaunchUtc is not { } last || nowUtc < last || nowUtc - last >= RelaunchWindow;

    /// <summary>
    /// Starts the bar again after <see cref="HangWatch"/> ended it, at most once per
    /// <see cref="RelaunchWindow"/>.
    /// </summary>
    /// <remarks>
    /// The hang watch cannot tell a hung bar from one stalled by a graphics driver reset or a slow
    /// sign-in, and ending a healthy bar used to leave the user on Windows' taskbar until they
    /// noticed and started Umbra by hand. Relaunching makes any such mistake cost a few seconds of
    /// Windows' taskbar. The window, rather than "once per bar", is so a bar that hangs on every
    /// start cannot loop, without making the second false positive of a long session permanent.
    /// No arguments: the settings file carries the configuration, and the command-line flags are
    /// development overrides that a guard has no way to read back off another process.
    /// </remarks>
    private static void RelaunchAfterHang(Outcome outcome)
    {
        if (outcome == Outcome.AnotherBarIsRunning)
        {
            BringUpLog.Write("crash guard: not relaunching after the hang, a bar is already running");
            return;
        }

        string path = DefaultRelaunchStampPath;
        DateTime now = DateTime.UtcNow;
        DateTime? last = ReadStamp(path);
        if (!ShouldRelaunch(last, now))
        {
            BringUpLog.Write(
                $"crash guard: not relaunching after the hang, one was relaunched at {last:O}; Windows' taskbar stays");
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, now.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the stamp a bar that hangs on every start would be relaunched every time,
            // each one restoring Explorer in between. Bounded, but not worth risking for one bar.
            BringUpLog.Write(e, "crash guard: could not record the relaunch, so not relaunching");
            return;
        }

        Start("", "bar, after ending a hung one,");
    }

    private static DateTime? ReadStamp(string path)
    {
        try
        {
            return long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out long ticks) &&
                   ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
                ? new DateTime(ticks, DateTimeKind.Utc)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    /// <summary>
    /// Blocks until the bar process has exited, or returns at once if it already has. True if
    /// the guard ended it for not answering.
    /// </summary>
    private static bool WaitForBar(int barId)
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
                return false;
            }

            BringUpLog.Write($"crash guard: watching bar {barId}");
            return Watch(bar);
        }
        catch (ArgumentException)
        {
            // Gone before we looked — a bar that died during startup. The lock and the marker
            // still decide what happens next.
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }

    /// <summary>
    /// The guard's wait on a bar it has identified. Apart from <see cref="WaitForBar"/> so a test
    /// can hand it a process that hangs: reverting this to <c>bar.WaitForExit()</c> passed every
    /// other test in the suite.
    /// </summary>
    /// <param name="interval">Between checks; the real <see cref="HangWatch.Interval"/> unless a test is in a hurry.</param>
    internal static bool Watch(Process bar, TimeSpan? interval = null) =>
        // Not a plain WaitForExit: a hung bar never exits, and keeps the taskbar suppressed.
        HangWatch.WaitForExitOrHang(bar, interval);

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

        public bool? GetSavedAutoHide() => inner.GetSavedAutoHide();

        public void SetSavedAutoHide(bool autoHide) => inner.SetSavedAutoHide(autoHide);

        public string? GetShellIdentity() => inner.GetShellIdentity();

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
        _ => "it left no marker, so it had restored Explorer itself (a clean exit, or a crash its own handlers caught)",
    };
}
