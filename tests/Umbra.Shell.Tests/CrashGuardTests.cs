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

    public void Dispose()
    {
        if (File.Exists(_statePath)) File.Delete(_statePath);
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
    [InlineData(new string[0], false)]
    public void Only_A_Leading_Guard_Flag_Makes_A_Guard(string[] args, bool expected) =>
        Assert.Equal(expected, CrashGuard.IsGuardLaunch(args));
}
