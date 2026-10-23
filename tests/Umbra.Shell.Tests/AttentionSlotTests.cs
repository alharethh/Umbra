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
    public void Its_Window_Closing_Clears_It()
    {
        (TaskbarSlot slot, ApplicationWindow window, _) = Build();

        window.State = ApplicationWindow.WindowState.Flashing;
        slot.SetWindows([]);
        Assert.Equal(AttentionState.None, slot.Attention);
    }
}
