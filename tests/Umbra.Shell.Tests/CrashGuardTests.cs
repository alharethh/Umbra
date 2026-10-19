using System.IO;
using ManagedShell.Interop;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// What the crash guard does once the bar it watches has gone.
/// </summary>
/// <remarks>
/// <para>
/// The waiting is not tested here — it is <c>Process.WaitForExit</c> — and was checked on hardware
/// with <c>Stop-Process -Id</c> instead. What is tested is the decision afterwards, because every
/// way of getting it wrong is silent: restoring under a live bar hands Explorer's taskbar back
/// beneath it, and capturing with no marker leaves one behind that the next launch then
/// "recovers" from.
/// </para>
/// <para>
/// The lock is claimed on another thread where it has to be held, because a mutex is owned by a
/// thread and a second claim on the same one succeeds recursively.
/// </para>
/// </remarks>
public class CrashGuardTests : IDisposable
{
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"umbra-guard-test-{Guid.NewGuid():N}");
    private readonly string _lockName = $@"Local\umbra-guard-test-{Guid.NewGuid():N}";

    private readonly string _stampPath = Path.Combine(Path.GetTempPath(), $"umbra-relaunch-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (File.Exists(_statePath)) File.Delete(_statePath);
        if (File.Exists(_stampPath)) File.Delete(_stampPath);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeTaskbar(NativeMethods.ABState state, int? alignment) : ITaskbarState
    {
        public NativeMethods.ABState State { get; private set; } = state;

        public int? Alignment { get; private set; } = alignment;

        public bool Hidden { get; set; }

        public NativeMethods.ABState GetState() => State;

        public void SetState(NativeMethods.ABState value) => State = value;

        public int? GetAlignment() => Alignment;

        public void SetAlignment(int? value) => Alignment = value;

        public bool? SavedAutoHide { get; set; } = false;

        public string? Shell { get; set; } = "explorer-1";

        public bool? GetSavedAutoHide() => SavedAutoHide;

        public void SetSavedAutoHide(bool value) => SavedAutoHide = value;

        public string? GetShellIdentity() => Shell;
    }

    /// <summary>What a bar leaves behind when it dies: its marker, and Explorer still suppressed.</summary>
    private FakeTaskbar BarDiedHoldingTheTaskbar(NativeMethods.ABState usersState, int? usersAlignment)
    {
        FakeTaskbar taskbar = new(usersState, usersAlignment);

        new ExplorerStateGuard(taskbar, _statePath).Capture();
        taskbar.SetState(NativeMethods.ABState.AutoHide);
        taskbar.SetAlignment(0);
        taskbar.Hidden = true;

        return taskbar;
    }

    [Fact]
    public void A_Bar_That_Died_Gets_The_Users_Taskbar_Back()
    {
        FakeTaskbar taskbar = BarDiedHoldingTheTaskbar(NativeMethods.ABState.Default, usersAlignment: 1);

        CrashGuard.Outcome outcome = CrashGuard.RecoverIfOrphaned(_lockName, _statePath, () => taskbar);

        Assert.Equal(CrashGuard.Outcome.Restored, outcome);
        Assert.Equal(NativeMethods.ABState.Default, taskbar.State);
        Assert.Equal(1, taskbar.Alignment);
        Assert.False(taskbar.Hidden);

        // Or the next launch would recover a second time from a record that is now stale.
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void The_Guard_Also_Repairs_Auto_Hide_Saved_By_An_Explorer_Restart()
    {
        // The two fixes composed: Explorer restarted under the bar and saved the suppression as
        // the user's setting, then the bar was killed. The guard's restore is the next launch's
        // recovery, so it puts the saved bit back too.
        FakeTaskbar taskbar = BarDiedHoldingTheTaskbar(NativeMethods.ABState.Default, usersAlignment: 1);
        taskbar.Shell = "explorer-2";
        taskbar.SavedAutoHide = true;

        CrashGuard.RecoverIfOrphaned(_lockName, _statePath, () => taskbar);

        Assert.False(taskbar.SavedAutoHide);
    }

    [Fact]
    public void An_Absent_Alignment_Is_Deleted_Rather_Than_Written()
    {
        FakeTaskbar taskbar = BarDiedHoldingTheTaskbar(NativeMethods.ABState.Default, usersAlignment: null);

        CrashGuard.RecoverIfOrphaned(_lockName, _statePath, () => taskbar);

        Assert.Null(taskbar.Alignment);
    }

    [Fact]
    public void After_A_Clean_Exit_It_Does_Not_Touch_Explorer_At_All()
    {
        // No marker: the bar restored Explorer and deleted it on the way out.
        bool reached = false;

        CrashGuard.Outcome outcome = CrashGuard.RecoverIfOrphaned(_lockName, _statePath, () =>
        {
            reached = true;
            return new FakeTaskbar(NativeMethods.ABState.AutoHide, 0);
        });

        Assert.Equal(CrashGuard.Outcome.NothingToDo, outcome);
        Assert.False(reached);

        // Capture with no marker records the current state and writes one, which would leave a
        // marker for a bar that is not running.
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void A_Running_Bar_Keeps_Its_Marker_And_Its_Suppression()
    {
        // A replacement bar has started and claimed the lock: the marker on disk is now its own.
        FakeTaskbar taskbar = BarDiedHoldingTheTaskbar(NativeMethods.ABState.Default, usersAlignment: 1);

        using ManualResetEventSlim claimed = new();
        using ManualResetEventSlim done = new();

        Thread replacement = new(() =>
        {
            using SingleInstance? bar = SingleInstance.Claim(_lockName);
            claimed.Set();
            done.Wait();
        });
        replacement.Start();
        claimed.Wait();

        try
        {
            CrashGuard.Outcome outcome = CrashGuard.RecoverIfOrphaned(_lockName, _statePath, () => taskbar);

            Assert.Equal(CrashGuard.Outcome.AnotherBarIsRunning, outcome);
            Assert.Equal(NativeMethods.ABState.AutoHide, taskbar.State);
            Assert.True(taskbar.Hidden);
            Assert.True(File.Exists(_statePath));
        }
        finally
        {
            done.Set();
            replacement.Join();
        }
    }

    [Fact]
    public void A_Lock_Left_By_A_Dead_Bar_Is_Taken_Not_Refused()
    {
        // The bar dies holding the lock, so the guard finds it abandoned. Treating that as "another
        // bar is running" would make the guard useless in exactly the case it exists for.
        FakeTaskbar taskbar = BarDiedHoldingTheTaskbar(NativeMethods.ABState.Default, usersAlignment: 1);

        // Kept reachable: finalising its Mutex would destroy the named object and the guard would
        // claim a fresh one, passing without ever meeting an abandoned lock.
        SingleInstance? dead = null;
        Thread dyingBar = new(() => dead = SingleInstance.Claim(_lockName));
        dyingBar.Start();
        dyingBar.Join();

        CrashGuard.Outcome outcome = CrashGuard.RecoverIfOrphaned(
            _lockName, _statePath, () => taskbar, lockTimeout: TimeSpan.FromSeconds(5));
        GC.KeepAlive(dead);

        Assert.Equal(CrashGuard.Outcome.Restored, outcome);
        Assert.Equal(NativeMethods.ABState.Default, taskbar.State);
    }

    [Fact]
    public void The_Lock_Is_Free_Again_Once_The_Guard_Is_Done()
    {
        // A relaunch after the guard has run must not be refused as a duplicate.
        BarDiedHoldingTheTaskbar(NativeMethods.ABState.Default, usersAlignment: 1);
        CrashGuard.RecoverIfOrphaned(_lockName, _statePath, () => new FakeTaskbar(NativeMethods.ABState.AutoHide, 0));

        SingleInstance? next = null;
        Thread relaunch = new(() =>
        {
            next = SingleInstance.Claim(_lockName);
            next?.Dispose();
        });
        relaunch.Start();
        relaunch.Join();

        Assert.NotNull(next);
    }

    [Theory]
    [InlineData(new[] { "--guard", "1234" }, true)]
    [InlineData(new[] { "--GUARD", "1234" }, true)]
    [InlineData(new[] { "--wait-for", "1234" }, false)]
    [InlineData(new[] { "--edge", "left", "--guard", "1" }, false)]
    [InlineData(new[] { "--guard-launch", "1234" }, false)]
    [InlineData(new string[0], false)]
    public void Only_A_Leading_Guard_Flag_Makes_A_Guard(string[] args, bool expected) =>
        Assert.Equal(expected, CrashGuard.IsGuardLaunch(args));

    // The launcher is what keeps the guard out of the bar's process tree. If the two flags could
    // be confused, the launcher would become a guard that is still the bar's child — and nothing
    // would look wrong until a tree kill took both.
    [Theory]
    [InlineData(new[] { "--guard-launch", "1234" }, true)]
    [InlineData(new[] { "--GUARD-LAUNCH", "1234" }, true)]
    [InlineData(new[] { "--guard", "1234" }, false)]
    [InlineData(new[] { "--edge", "left", "--guard-launch", "1" }, false)]
    [InlineData(new string[0], false)]
    public void Only_A_Leading_Launcher_Flag_Makes_A_Launcher(string[] args, bool expected) =>
        Assert.Equal(expected, CrashGuard.IsLauncherLaunch(args));

    // These go through the functions Launch and RunLauncher really call, and Start takes nothing
    // but a finished command line, so they fail if either hop starts the wrong flag.
    // Mutation-checked: the bar starting --guard directly (the guard back inside its process
    // tree), and the launcher starting another launcher (an endless chain, no guard), each turn
    // this red.
    [Fact]
    public void The_Bar_Starts_A_Launcher_Not_A_Guard()
    {
        string[] launcher = CrashGuard.LauncherArguments(4321).Split(' ');

        Assert.True(CrashGuard.IsLauncherLaunch(launcher));
        Assert.False(CrashGuard.IsGuardLaunch(launcher));
        Assert.Equal("4321", launcher[1]);
    }

    [Fact]
    public void The_Launcher_Starts_A_Guard_For_The_Same_Bar_Not_Another_Launcher()
    {
        string? guardArgs = CrashGuard.GuardArgumentsFor(CrashGuard.LauncherArguments(4321).Split(' '));

        Assert.NotNull(guardArgs);
        string[] guard = guardArgs.Split(' ');
        Assert.True(CrashGuard.IsGuardLaunch(guard));
        Assert.False(CrashGuard.IsLauncherLaunch(guard));
        Assert.Equal("4321", guard[1]);
    }

    [Theory]
    [InlineData("--guard-launch")]
    [InlineData("--guard-launch abc")]
    [InlineData("--guard-launch ")]
    [InlineData("--guard 1234")]
    [InlineData("")]
    public void A_Launcher_Without_A_Bar_Id_Starts_Nothing(string commandLine) =>
        Assert.Null(CrashGuard.GuardArgumentsFor(commandLine.Length == 0 ? [] : commandLine.Split(' ')));

    // A bar the hang watch ended may have been healthy and merely stalled (a driver reset, a slow
    // sign-in), so the guard starts it again — but not every time, or a bar that hangs on every
    // start would be ended and relaunched for ever.

    private static readonly DateTime Now = new(2026, 10, 19, 23, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_First_Hang_Is_Relaunched() =>
        Assert.True(CrashGuard.ShouldRelaunch(null, Now));

    [Fact]
    public void A_Second_Hang_Soon_After_A_Relaunch_Is_Not() =>
        Assert.False(CrashGuard.ShouldRelaunch(Now - TimeSpan.FromSeconds(20), Now));

    [Fact]
    public void A_Hang_Long_After_The_Last_Relaunch_Is_Relaunched_Again() =>
        Assert.True(CrashGuard.ShouldRelaunch(Now - CrashGuard.RelaunchWindow, Now));

    [Fact]
    public void A_Clock_That_Went_Backwards_Does_Not_Strand_The_User() =>
        Assert.True(CrashGuard.ShouldRelaunch(Now + TimeSpan.FromHours(1), Now));

    [Fact]
    public void The_Relaunch_Window_Outlasts_A_Bar_That_Hangs_On_Every_Start()
    {
        // A bar that hangs at once is ended after at most ~17 s, plus the start before it. The
        // window has to cover that comfortably, or the "once" is not once.
        Assert.True(CrashGuard.RelaunchWindow >= TimeSpan.FromMinutes(2));
    }

    // What Run does once the bar is gone, driven through the same method Run calls, with a fake
    // start. Every refusal below is a bar started against the user's wishes if it goes: after
    // their Exit, against a Restart's replacement, or after a crash the guard did not cause.

    private int _starts;

    private bool Conclude(bool ended, bool exitDeclared, CrashGuard.Outcome outcome, bool startSucceeds = true) =>
        CrashGuard.ConcludeAfterBar(ended, exitDeclared, outcome, _stampPath, Now, () =>
        {
            _starts++;
            return startSucceeds;
        });

    [Fact]
    public void A_Hung_Bar_That_Was_Not_Leaving_Is_Started_Again_And_Recorded()
    {
        Assert.True(Conclude(ended: true, exitDeclared: false, CrashGuard.Outcome.Restored));
        Assert.Equal(1, _starts);
        Assert.True(File.Exists(_stampPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Bar_The_Guard_Did_Not_End_Is_Never_Started_Again(bool restored)
    {
        // A crash, a kill, Task Manager, or a clean exit: the guard restores and leaves it there.
        Assert.False(Conclude(ended: false, exitDeclared: false, restored ? CrashGuard.Outcome.Restored : CrashGuard.Outcome.NothingToDo));
        Assert.Equal(0, _starts);
        Assert.False(File.Exists(_stampPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Bar_That_Stalled_On_Its_Way_Out_Is_Not_Started_Again(bool restored)
    {
        // Exit, Restart, dev-restart or logoff, then a stall before (Restored) or after
        // (NothingToDo) ShellHost handed the taskbar back. The hang watch ends it; that is right.
        Assert.False(Conclude(ended: true, exitDeclared: true, restored ? CrashGuard.Outcome.Restored : CrashGuard.Outcome.NothingToDo));
        Assert.Equal(0, _starts);
    }

    [Fact]
    public void A_Bar_That_Had_Already_Restored_Explorer_Is_Not_Started_Again_Even_Undeclared()
    {
        // No marker means ShellHost.Dispose had run, which only an exit reaches: the backstop for
        // a bar whose exit intent could not be declared.
        Assert.False(Conclude(ended: true, exitDeclared: false, CrashGuard.Outcome.NothingToDo));
        Assert.Equal(0, _starts);
    }

    [Fact]
    public void A_Hung_Bar_Is_Not_Started_Again_When_Another_Bar_Holds_The_Lock()
    {
        Assert.False(Conclude(ended: true, exitDeclared: false, CrashGuard.Outcome.AnotherBarIsRunning));
        Assert.Equal(0, _starts);
    }

    [Fact]
    public void A_Second_Hang_Inside_The_Window_Is_Not_Started_Again()
    {
        Assert.True(Conclude(ended: true, exitDeclared: false, CrashGuard.Outcome.Restored));
        Assert.False(Conclude(ended: true, exitDeclared: false, CrashGuard.Outcome.Restored));
        Assert.Equal(1, _starts);
    }

    [Fact]
    public void A_Failed_Start_Still_Uses_The_Allowance()
    {
        // The stamp is the loop bound, so it is written first and kept.
        Assert.False(Conclude(ended: true, exitDeclared: false, CrashGuard.Outcome.Restored, startSucceeds: false));
        Assert.True(File.Exists(_stampPath));
        Assert.False(Conclude(ended: true, exitDeclared: false, CrashGuard.Outcome.Restored));
        Assert.Equal(1, _starts);
    }

    [Fact]
    public void An_Exit_Declared_By_The_Bar_Is_Seen_By_A_Guard_Whichever_Opens_First()
    {
        // Ids no real process has, so the test cannot see a real bar's event or set one.
        int guardFirst = -Random.Shared.Next(1, int.MaxValue);
        int barFirst = guardFirst == -1 ? -2 : guardFirst + 1;

        using var watched = ExitIntent.Watch(guardFirst);
        Assert.False(ExitIntent.WasDeclared(watched));
        ExitIntent.Declare(guardFirst, "test");
        Assert.True(ExitIntent.WasDeclared(watched));

        ExitIntent.Declare(barFirst, "test");
        using var late = ExitIntent.Watch(barFirst);
        Assert.True(ExitIntent.WasDeclared(late));
    }

    [Fact]
    public void A_Guard_Without_An_Exit_Intent_Reads_It_As_Undeclared() =>
        Assert.False(ExitIntent.WasDeclared(null));
}
