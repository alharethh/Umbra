using System.IO;
using ManagedShell.Interop;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// Crash recovery for Explorer's taskbar state.
/// </summary>
/// <remarks>
/// <para>
/// These run against a fake because the interesting case cannot be exercised on a machine whose
/// taskbar already autohides — there, restoring is a no-op and a broken guard would look identical
/// to a working one. Suppressing Explorer's taskbar works by switching it to autohide, so a run
/// that dies without restoring leaves the user's taskbar invisible until they go and fix it by
/// hand. That is the failure this guards, and it is silent.
/// </para>
/// <para>
/// The ordering is the whole trick: after an unclean exit the state observed at startup is already
/// the damage, so recovery has to read the persisted value <i>before</i> capturing anything.
/// </para>
/// </remarks>
public class ExplorerStateGuardTests : IDisposable
{
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"umbra-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (File.Exists(_statePath)) File.Delete(_statePath);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeTaskbar : ITaskbarState
    {
        public FakeTaskbar(NativeMethods.ABState initial, int? alignment = null)
        {
            State = initial;
            Alignment = alignment;
        }

        public NativeMethods.ABState State { get; private set; }

        public int? Alignment { get; private set; }

        public bool Hidden { get; set; }

        public int SetStateCalls { get; private set; }

        public NativeMethods.ABState GetState() => State;

        public void SetState(NativeMethods.ABState state)
        {
            State = state;
            SetStateCalls++;
        }

        public int? GetAlignment() => Alignment;

        public void SetAlignment(int? alignment) => Alignment = alignment;

        /// <summary>The saved Settings toggle; null stands for a blob in an unfamiliar format.</summary>
        public bool? SavedAutoHide { get; set; } = false;

        public int SavedAutoHideWrites { get; private set; }

        /// <summary>Which Explorer is running the shell.</summary>
        public string? Shell { get; set; } = "explorer-1";

        public bool? GetSavedAutoHide() => SavedAutoHide;

        public void SetSavedAutoHide(bool autoHide)
        {
            // Like the live one: an unfamiliar blob is never written.
            if (SavedAutoHide is null) return;

            SavedAutoHide = autoHide;
            SavedAutoHideWrites++;
        }

        public string? GetShellIdentity() => Shell;

        private Action? _onSave;

        /// <summary>Whether a watch on the saved settings is open.</summary>
        public bool Watching => _onSave is not null;

        public int WatchesOpened { get; private set; }

        public IDisposable? WatchSavedSettings(Action onChange)
        {
            // Like the live one: a call straight away, then one per write.
            _onSave = onChange;
            WatchesOpened++;
            onChange();
            return new Unwatch(this, onChange);
        }

        private sealed class Unwatch(FakeTaskbar owner, Action onChange) : IDisposable
        {
            public void Dispose()
            {
                if (owner._onSave == onChange) owner._onSave = null;
            }
        }

        /// <summary>
        /// QA 2026-10-18, case 0.6: the restarted Explorer saves its settings some seconds after
        /// starting, after the bar's reservation has already settled.
        /// </summary>
        public void ExplorerSavesItsSettings(bool autoHide)
        {
            SavedAutoHide = autoHide;
            _onSave?.Invoke();
        }

        /// <summary>
        /// QA case 0.6: Explorer is killed and Windows starts a new one, which saves the
        /// suppression it was handed while starting up as the user's own setting.
        /// </summary>
        public void ExplorerRestartsUnderTheBar()
        {
            Shell += "-restarted";
            SavedAutoHide = true;
        }

        /// <summary>What the shell does to suppress Explorer: autohide, hide, and claim alignment.</summary>
        public void SuppressLikeUmbraDoes()
        {
            State = NativeMethods.ABState.AutoHide;
            Hidden = true;
            Alignment = 0;
        }
    }

    [Fact]
    public void Restores_A_Taskbar_That_Was_Not_Autohiding()
    {
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default);
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        taskbar.SuppressLikeUmbraDoes();
        guard.Restore();

        Assert.Equal(NativeMethods.ABState.Default, taskbar.State);
        Assert.False(taskbar.Hidden);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void Leaves_A_Taskbar_That_Was_Already_Autohiding_Alone()
    {
        // The configuration on the development machine, where a broken guard is invisible.
        FakeTaskbar taskbar = new(NativeMethods.ABState.AutoHide);
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        taskbar.SuppressLikeUmbraDoes();
        guard.Restore();

        Assert.Equal(NativeMethods.ABState.AutoHide, taskbar.State);
        Assert.False(taskbar.Hidden);
    }

    [Fact]
    public void Repairs_The_Damage_Left_By_A_Run_That_Was_Hard_Killed()
    {
        // First run: taskbar was not autohiding, Umbra suppresses it, process is killed outright.
        // No Restore, no ProcessExit handler, and the state file survives.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default);
        new ExplorerStateGuard(taskbar, _statePath).Capture();
        taskbar.SuppressLikeUmbraDoes();

        Assert.True(File.Exists(_statePath));
        Assert.Equal(NativeMethods.ABState.AutoHide, taskbar.State);

        // Second run finds the leftovers and puts the taskbar back before doing anything else.
        ExplorerStateGuard second = new(taskbar, _statePath);
        second.Capture();

        Assert.True(second.RecoveredFromUncleanExit);
        Assert.Equal(NativeMethods.ABState.Default, taskbar.State);

        second.Restore();
        Assert.Equal(NativeMethods.ABState.Default, taskbar.State);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void Does_Not_Record_The_Damage_As_The_Thing_To_Restore()
    {
        // The ordering bug this is written against: capture the live state first and the value
        // recorded after a crash is AutoHide, so every subsequent run faithfully restores the
        // damage and the user's taskbar never comes back.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default);
        new ExplorerStateGuard(taskbar, _statePath).Capture();
        taskbar.SuppressLikeUmbraDoes();

        ExplorerStateGuard second = new(taskbar, _statePath);
        second.Capture();

        Assert.Equal(NativeMethods.ABState.Default, second.OriginalState);
    }

    [Fact]
    public void Survives_Repeated_Unclean_Exits()
    {
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default);

        for (int run = 0; run < 5; run++)
        {
            ExplorerStateGuard guard = new(taskbar, _statePath);
            guard.Capture();
            taskbar.SuppressLikeUmbraDoes();
        }

        ExplorerStateGuard final = new(taskbar, _statePath);
        final.Capture();
        final.Restore();

        Assert.Equal(NativeMethods.ABState.Default, taskbar.State);
    }

    [Fact]
    public void A_Clean_Run_Leaves_No_Marker_Behind()
    {
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default);
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        Assert.True(File.Exists(_statePath));

        guard.Restore();
        Assert.False(File.Exists(_statePath));

        // And the next run must not think it is recovering from anything.
        ExplorerStateGuard next = new(taskbar, _statePath);
        next.Capture();
        Assert.False(next.RecoveredFromUncleanExit);
    }

    [Fact]
    public void Restore_Is_Idempotent()
    {
        // It is called from Dispose, from ProcessExit, and from both unhandled-exception handlers.
        // A crash can easily hit two of those.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default);
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        taskbar.SuppressLikeUmbraDoes();

        guard.Restore();
        int callsAfterFirst = taskbar.SetStateCalls;
        guard.Restore();
        guard.Dispose();

        Assert.Equal(callsAfterFirst, taskbar.SetStateCalls);
        Assert.Equal(NativeMethods.ABState.Default, taskbar.State);
    }

    [Fact]
    public void Restores_The_Taskbar_Alignment_Umbra_Overwrote()
    {
        // §7.0 writes TaskbarAl so the Start menu opens beneath our Start button. §11.2 Q4 widens
        // crash recovery to cover it, because a user who had a centred taskbar and finds it
        // left-aligned afterwards has been quietly reconfigured.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default, alignment: 1);
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        guard.ApplyAlignment(0);

        Assert.Equal(0, taskbar.Alignment);

        guard.Restore();
        Assert.Equal(1, taskbar.Alignment);
    }

    [Fact]
    public void An_Absent_Alignment_Is_Restored_As_Absent_Not_As_Zero()
    {
        // TaskbarAl missing means Windows is using its own default. Writing a value back that was
        // never there is still a change to the user's machine, so absence has to round-trip.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default, alignment: null);
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        guard.ApplyAlignment(0);
        guard.Restore();

        Assert.Null(taskbar.Alignment);
    }

    [Fact]
    public void Alignment_Survives_A_Hard_Kill()
    {
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default, alignment: 1);

        // First run claims the alignment, then dies without restoring.
        ExplorerStateGuard first = new(taskbar, _statePath);
        first.Capture();
        first.ApplyAlignment(0);
        taskbar.SuppressLikeUmbraDoes();

        Assert.Equal(0, taskbar.Alignment);

        // Second run repairs it before recording its own baseline.
        ExplorerStateGuard second = new(taskbar, _statePath);
        second.Capture();

        Assert.True(second.RecoveredFromUncleanExit);
        Assert.Equal(1, taskbar.Alignment);
        Assert.Equal(1, second.OriginalAlignment);

        second.Restore();
        Assert.Equal(1, taskbar.Alignment);
    }

    [Fact]
    public void An_Explorer_Restart_Under_The_Bar_Does_Not_Leave_Auto_Hide_Switched_On()
    {
        // QA case 0.6, the reason the saved bit is captured at all: the runtime state came back
        // and the saved setting stayed 0x03, so the user's next sign-in would hide their taskbar.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default) { SavedAutoHide = false };
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        taskbar.SuppressLikeUmbraDoes();
        taskbar.ExplorerRestartsUnderTheBar();
        guard.Restore();

        Assert.False(taskbar.SavedAutoHide);
        Assert.True(guard.RepairedSavedAutoHide);
        Assert.Equal(NativeMethods.ABState.Default, taskbar.State);
    }

    [Fact]
    public void A_Change_The_User_Makes_In_Settings_While_The_Bar_Runs_Is_Kept()
    {
        // Same Explorer throughout, so a different saved bit at exit is the user's doing. Reverting
        // it is the cost of the repair misfiring, which is why it waits for a restart.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default) { SavedAutoHide = false };
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        taskbar.SuppressLikeUmbraDoes();
        taskbar.SavedAutoHide = true;
        guard.Restore();

        Assert.True(taskbar.SavedAutoHide);
        Assert.Equal(0, taskbar.SavedAutoHideWrites);
    }

    [Fact]
    public void A_Restart_That_Saved_Nothing_Wrong_Writes_Nothing()
    {
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default) { SavedAutoHide = false };
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        taskbar.Shell = "explorer-2";
        guard.Restore();

        Assert.Equal(0, taskbar.SavedAutoHideWrites);
        Assert.False(guard.RepairedSavedAutoHide);
    }

    [Fact]
    public void An_Unfamiliar_Saved_Format_Is_Never_Written()
    {
        // Null is what the live reader returns for a blob it does not recognise.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default) { SavedAutoHide = null };
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        taskbar.Shell = "explorer-2";
        guard.Restore();

        Assert.Null(guard.OriginalSavedAutoHide);
        Assert.Equal(0, taskbar.SavedAutoHideWrites);
    }

    [Fact]
    public void No_Known_Explorer_At_Capture_Means_No_Repair()
    {
        // Without a baseline identity a restart cannot be told from the user's own change.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default) { SavedAutoHide = false, Shell = null };
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        taskbar.Shell = "explorer-2";
        taskbar.SavedAutoHide = true;
        guard.Restore();

        Assert.True(taskbar.SavedAutoHide);
    }

    [Fact]
    public void Crash_Recovery_Repairs_A_Bit_Saved_By_A_Restart_During_The_Dead_Run()
    {
        // Explorer restarts under the bar, then the bar is hard killed: the next launch must not
        // take 0x03 as the user's setting.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default) { SavedAutoHide = false };
        new ExplorerStateGuard(taskbar, _statePath).Capture();
        taskbar.SuppressLikeUmbraDoes();
        taskbar.ExplorerRestartsUnderTheBar();

        ExplorerStateGuard second = new(taskbar, _statePath);
        second.Capture();

        Assert.True(second.RecoveredFromUncleanExit);
        Assert.False(taskbar.SavedAutoHide);
        Assert.Equal(false, second.OriginalSavedAutoHide);

        // And the restored baseline holds for the rest of the run.
        taskbar.SuppressLikeUmbraDoes();
        second.Restore();
        Assert.False(taskbar.SavedAutoHide);
    }

    [Fact]
    public void Crash_Recovery_With_The_Same_Explorer_Leaves_The_Saved_Bit_Alone()
    {
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default) { SavedAutoHide = false };
        new ExplorerStateGuard(taskbar, _statePath).Capture();
        taskbar.SuppressLikeUmbraDoes();
        taskbar.SavedAutoHide = true;   // the user, in Settings, before the crash

        ExplorerStateGuard second = new(taskbar, _statePath);
        second.Capture();

        Assert.True(taskbar.SavedAutoHide);
        Assert.Equal(0, taskbar.SavedAutoHideWrites);
    }

    [Fact]
    public void A_Marker_From_An_Older_Build_Does_Not_Touch_The_Saved_Bit()
    {
        // No autohide or shell line: nothing was recorded, so nothing is known to put back.
        File.WriteAllLines(_statePath, ["state=0"]);

        FakeTaskbar taskbar = new(NativeMethods.ABState.AutoHide) { SavedAutoHide = true, Shell = "explorer-9" };
        ExplorerStateGuard guard = new(taskbar, _statePath);
        guard.Capture();
        guard.Restore();

        Assert.True(guard.RecoveredFromUncleanExit);
        Assert.True(taskbar.SavedAutoHide);
        Assert.Equal(0, taskbar.SavedAutoHideWrites);
    }

    [Fact]
    public void A_Corrupt_Marker_Does_Not_Stop_The_Bar_Starting()
    {
        File.WriteAllText(_statePath, "not a number");

        FakeTaskbar taskbar = new(NativeMethods.ABState.Default);
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();

        Assert.False(guard.RecoveredFromUncleanExit);
        Assert.Equal(NativeMethods.ABState.Default, guard.OriginalState);
    }

    /// <summary>The bar is running, Explorer has just been killed and Windows has started a new one.</summary>
    private (FakeTaskbar Taskbar, ExplorerStateGuard Guard) RunningBarAfterAnExplorerRestart()
    {
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default) { SavedAutoHide = false };
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();
        taskbar.SuppressLikeUmbraDoes();
        taskbar.Shell = "explorer-2";

        return (taskbar, guard);
    }

    [Fact]
    public void A_Late_Save_After_An_Explorer_Restart_Is_Put_Back_While_The_Bar_Runs()
    {
        // QA 2026-10-18, case 0.6 (backlog 1g): the save landed at +7.6 s, after the reservation
        // had settled, and stayed 0x03 until exit. Anything that ended the session without an
        // exit or a relaunch would have left the user's taskbar set to hide.
        (FakeTaskbar taskbar, ExplorerStateGuard guard) = RunningBarAfterAnExplorerRestart();

        guard.WatchForLateSave(TimeSpan.FromMinutes(1));
        Assert.Equal(0, taskbar.SavedAutoHideWrites);

        taskbar.ExplorerSavesItsSettings(autoHide: true);

        // Before Restore: this is the whole point.
        Assert.False(taskbar.SavedAutoHide);
        Assert.True(guard.RepairedSavedAutoHide);

        guard.Restore();
        Assert.False(taskbar.SavedAutoHide);
    }

    [Fact]
    public void A_Save_That_Landed_Before_The_Watch_Started_Is_Put_Back_At_Once()
    {
        (FakeTaskbar taskbar, ExplorerStateGuard guard) = RunningBarAfterAnExplorerRestart();
        taskbar.SavedAutoHide = true;

        guard.WatchForLateSave(TimeSpan.FromMinutes(1));

        Assert.False(taskbar.SavedAutoHide);
        Assert.Equal(1, taskbar.SavedAutoHideWrites);
    }

    [Fact]
    public void Every_Save_In_The_Window_Is_Put_Back_Not_Only_The_First()
    {
        (FakeTaskbar taskbar, ExplorerStateGuard guard) = RunningBarAfterAnExplorerRestart();
        guard.WatchForLateSave(TimeSpan.FromMinutes(1));

        taskbar.ExplorerSavesItsSettings(autoHide: true);
        taskbar.ExplorerSavesItsSettings(autoHide: true);

        Assert.False(taskbar.SavedAutoHide);
        Assert.Equal(2, taskbar.SavedAutoHideWrites);
    }

    [Fact]
    public void A_Save_Without_A_Restart_Is_The_Users_And_Is_Kept()
    {
        // The watch may be started by a broadcast that was not a restart at all. The repair's own
        // condition, a different Explorer, is what keeps it from reverting the user.
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default) { SavedAutoHide = false };
        ExplorerStateGuard guard = new(taskbar, _statePath);
        guard.Capture();
        taskbar.SuppressLikeUmbraDoes();

        guard.WatchForLateSave(TimeSpan.FromMinutes(1));
        taskbar.ExplorerSavesItsSettings(autoHide: true);

        Assert.True(taskbar.SavedAutoHide);
        Assert.Equal(0, taskbar.SavedAutoHideWrites);
    }

    [Fact]
    public void Restore_Ends_The_Watch()
    {
        (FakeTaskbar taskbar, ExplorerStateGuard guard) = RunningBarAfterAnExplorerRestart();
        guard.WatchForLateSave(TimeSpan.FromMinutes(1));

        guard.Restore();

        Assert.False(taskbar.Watching);
        Assert.False(guard.WatchingForLateSave);

        // And a watch asked for after exit is never opened.
        guard.WatchForLateSave(TimeSpan.FromMinutes(1));
        Assert.Equal(1, taskbar.WatchesOpened);
    }

    [Fact]
    public void The_Watch_Ends_When_Its_Window_Does()
    {
        (FakeTaskbar taskbar, ExplorerStateGuard guard) = RunningBarAfterAnExplorerRestart();
        guard.WatchForLateSave(TimeSpan.FromMilliseconds(20));

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (guard.WatchingForLateSave && DateTime.UtcNow < deadline) Thread.Sleep(10);

        Assert.False(guard.WatchingForLateSave);
        Assert.False(taskbar.Watching);
    }

    [Fact]
    public void Repairs_Per_Watch_Are_Bounded()
    {
        // In case Explorer ever answers our write with one of its own: a bounded exchange, then
        // the exit repair has the last word as before.
        (FakeTaskbar taskbar, ExplorerStateGuard guard) = RunningBarAfterAnExplorerRestart();
        guard.WatchForLateSave(TimeSpan.FromMinutes(1));

        for (int i = 0; i < ExplorerStateGuard.MaxRepairsPerWatch + 3; i++)
            taskbar.ExplorerSavesItsSettings(autoHide: true);

        Assert.Equal(ExplorerStateGuard.MaxRepairsPerWatch, taskbar.SavedAutoHideWrites);

        guard.Restore();
        Assert.False(taskbar.SavedAutoHide);
    }

    [Fact]
    public void Each_Restart_Gets_Its_Own_Repair_Allowance()
    {
        // Backlog 1i (c): the bound is per watch, not per run. A second restart that used up
        // nothing must still be put right, or a run with two Explorer crashes keeps the damage.
        (FakeTaskbar taskbar, ExplorerStateGuard guard) = RunningBarAfterAnExplorerRestart();
        guard.WatchForLateSave(TimeSpan.FromMinutes(1));

        for (int i = 0; i < ExplorerStateGuard.MaxRepairsPerWatch; i++)
            taskbar.ExplorerSavesItsSettings(autoHide: true);

        Assert.Equal(ExplorerStateGuard.MaxRepairsPerWatch, taskbar.SavedAutoHideWrites);

        taskbar.ExplorerRestartsUnderTheBar();
        guard.WatchForLateSave(TimeSpan.FromMinutes(1));
        taskbar.ExplorerSavesItsSettings(autoHide: true);

        Assert.False(taskbar.SavedAutoHide);
        Assert.True(taskbar.SavedAutoHideWrites > ExplorerStateGuard.MaxRepairsPerWatch);
    }

    /// <summary>Timers that fire only when the test says so, including after being disposed.</summary>
    private sealed class ManualTimers
    {
        public List<Action> Callbacks { get; } = [];

        public IDisposable Start(TimeSpan due, Action callback)
        {
            Callbacks.Add(callback);
            return new Nothing();
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public void A_Late_Timer_From_An_Earlier_Watch_Does_Not_End_A_Newer_One()
    {
        // Backlog 1i (b): disposing a timer does not withdraw a callback already queued, so the
        // first window can expire just as a second restart replaces its watch. That callback
        // must not end the second watch, two minutes early.
        ManualTimers timers = new();
        FakeTaskbar taskbar = new(NativeMethods.ABState.Default) { SavedAutoHide = false };
        ExplorerStateGuard guard = new(taskbar, _statePath, timers.Start);
        guard.Capture();
        taskbar.SuppressLikeUmbraDoes();
        taskbar.Shell = "explorer-2";

        guard.WatchForLateSave(TimeSpan.FromMinutes(2));
        taskbar.ExplorerRestartsUnderTheBar();
        guard.WatchForLateSave(TimeSpan.FromMinutes(2));
        Assert.Equal(2, timers.Callbacks.Count);

        timers.Callbacks[0]();

        Assert.True(guard.WatchingForLateSave);
        Assert.True(taskbar.Watching);

        taskbar.ExplorerSavesItsSettings(autoHide: true);
        Assert.False(taskbar.SavedAutoHide);

        // Its own timer still ends it.
        timers.Callbacks[1]();
        Assert.False(guard.WatchingForLateSave);
        Assert.False(taskbar.Watching);
    }
}
