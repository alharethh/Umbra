using ManagedShell.WindowsTasks;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// The slot's wiring to the latch: a window's state changing reaches the badge, and so does being
/// switched to. The latch tests cannot see either.
/// </summary>
public class AttentionSlotTests
{
    private static (TaskbarSlot Slot, ApplicationWindow Window, Func<double, double> SetClock) Build()
    {
        // ManagedShell's window takes a null service and an arbitrary handle; State is a plain
        // property that raises PropertyChanged, which is exactly what its shell hook does.
        ApplicationWindow window = new(null!, 0x1234);
        TaskbarSlot slot = new("test-app", pin: null);
        double now = 1000;
        slot.Clock = () => now;
        slot.FlashingEnabled = () => true;
        slot.SetWindows([window]);
        return (slot, window, t => now = t);
    }

    [Fact]
    public void A_Flash_Reaches_The_Badge_And_Its_Gaps_Do_Not_End_It()
    {
        (TaskbarSlot slot, ApplicationWindow window, Func<double, double> setClock) = Build();
        Assert.Equal(AttentionState.None, slot.Attention);

        // HSHELL_FLASH, then the HSHELL_REDRAW that turns the button off again.
        window.State = ApplicationWindow.WindowState.Flashing;
        Assert.Equal(AttentionState.Pulsing, slot.Attention);

        setClock(1000.53);
        window.State = ApplicationWindow.WindowState.Inactive;
        Assert.Equal(AttentionState.Pulsing, slot.Attention);

        slot.Detach();
    }

    [Fact]
    public void Being_Switched_To_Clears_It()
    {
        (TaskbarSlot slot, ApplicationWindow window, _) = Build();

        window.State = ApplicationWindow.WindowState.Flashing;
        Assert.Equal(AttentionState.Pulsing, slot.Attention);

        slot.SetForeground(window.Handle);
        Assert.Equal(AttentionState.None, slot.Attention);

        slot.Detach();
    }

    [Fact]
    public void A_Withdrawn_Flash_Wakes_The_Slot_And_Ends_The_Badge()
    {
        (TaskbarSlot slot, ApplicationWindow window, Func<double, double> setClock) = Build();

        window.State = ApplicationWindow.WindowState.Flashing;
        setClock(1000.53);
        window.State = ApplicationWindow.WindowState.Inactive;

        // Nothing else will ask after FLASHW_STOP, so the slot has to have woken itself.
        Assert.NotNull(slot.WakeDueIn);
        Assert.Equal(AttentionLatch.QuietSeconds, slot.WakeDueIn!.Value, 6);

        // What the wake-up does.
        setClock(1000.53 + AttentionLatch.QuietSeconds + 0.05);
        slot.UpdateAttention();
        Assert.Equal(AttentionState.None, slot.Attention);
        Assert.Null(slot.WakeDueIn);

        slot.Detach();
    }

    [Fact]
    public void While_Lit_The_Next_Wake_Up_Is_The_Decay()
    {
        (TaskbarSlot slot, ApplicationWindow window, _) = Build();

        window.State = ApplicationWindow.WindowState.Flashing;
        Assert.Equal(Umbra.Core.Tiers.NotificationBadge.DecayAfterSeconds, slot.WakeDueIn!.Value, 6);

        slot.Detach();
    }

    [Fact]
    public void Windows_Setting_Off_Hides_It_And_Holds_No_Timer()
    {
        (TaskbarSlot slot, ApplicationWindow window, _) = Build();
        slot.FlashingEnabled = () => false;

        window.State = ApplicationWindow.WindowState.Flashing;
        Assert.Equal(AttentionState.None, slot.Attention);
        Assert.Null(slot.WakeDueIn);

        slot.Detach();
    }

    [Fact]
    public void Its_Window_Closing_Clears_It()
    {
        (TaskbarSlot slot, ApplicationWindow window, _) = Build();

        window.State = ApplicationWindow.WindowState.Flashing;
        slot.SetWindows([]);
        Assert.Equal(AttentionState.None, slot.Attention);
    }
}
