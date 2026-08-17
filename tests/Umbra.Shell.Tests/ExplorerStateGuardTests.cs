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
    public void A_Corrupt_Marker_Does_Not_Stop_The_Bar_Starting()
    {
        File.WriteAllText(_statePath, "not a number");

        FakeTaskbar taskbar = new(NativeMethods.ABState.Default);
        ExplorerStateGuard guard = new(taskbar, _statePath);

        guard.Capture();

        Assert.False(guard.RecoveredFromUncleanExit);
        Assert.Equal(NativeMethods.ABState.Default, guard.OriginalState);
    }
}
