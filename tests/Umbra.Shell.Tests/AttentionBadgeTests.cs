using System.Windows.Media;
using System.Windows.Media.Animation;
using Umbra.Core.Tiers;
using Umbra.Shell.Rendering;
using Umbra.Shell.Shell;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// The attention badge: an application that flashes its taskbar button gets a pulsing dot until it
/// is switched to (§3.3).
/// </summary>
public class AttentionBadgeTests
{
    [Fact]
    public void A_Flash_Raises_The_Badge_And_Switching_To_It_Clears_It()
    {
        AttentionLatch latch = new();

        Assert.Equal(AttentionState.None, latch.Update(flashing: false, active: false, enabled: true, now: 0));
        Assert.Equal(AttentionState.Pulsing, latch.Update(flashing: true, active: false, enabled: true, now: 1));
        Assert.Equal(AttentionState.None, latch.Update(flashing: true, active: true, enabled: true, now: 2));
    }

    [Fact]
    public void A_State_That_Lingers_After_Being_Seen_Does_Not_Bring_It_Back()
    {
        // ManagedShell's window state can go stale. Once the window has been in front, the request
        // has been seen, and leaving it again must not raise the badge a second time.
        AttentionLatch latch = new();

        latch.Update(true, false, true, 0);
        latch.Update(true, true, true, 1);

        Assert.Equal(AttentionState.None, latch.Update(flashing: true, active: false, enabled: true, now: 2));

        // A new request after it stopped asking is a new badge.
        latch.Update(false, false, true, 3);
        Assert.Equal(AttentionState.Pulsing, latch.Update(true, false, true, 4));
    }

    [Fact]
    public void The_Badge_Stays_Up_For_As_Long_As_The_Request_Does()
    {
        // FLASHW_TIMERNOFG flashes forever; Windows keeps the button lit after its seven flashes.
        // Either way the badge follows the request, not each flash.
        AttentionLatch latch = new();

        latch.Update(true, false, true, 0);
        Assert.Equal(AttentionState.Pulsing, latch.Update(true, false, true, 600));
    }

    [Fact]
    public void An_Ignored_Badge_Decays_After_Fifteen_Minutes()
    {
        AttentionLatch latch = new();

        latch.Update(true, false, true, 100);
        Assert.Equal(100 + NotificationBadge.DecayAfterSeconds, latch.DecaysAt);
        Assert.Equal(AttentionState.Pulsing, latch.Update(true, false, true, 100 + 899));
        Assert.Equal(AttentionState.Decayed, latch.Update(true, false, true, 100 + 900));
    }

    [Fact]
    public void Windows_Setting_Off_Hides_It()
    {
        AttentionLatch latch = new();
        Assert.Equal(AttentionState.None, latch.Update(flashing: true, active: false, enabled: false, now: 0));
        Assert.Equal(AttentionState.Pulsing, latch.Update(flashing: true, active: false, enabled: true, now: 1));
    }

    [Fact]
    public void Only_An_Explicit_Zero_Turns_Flashing_Off()
    {
        // Absent is Windows' default, which is on.
        Assert.True(TaskbarFlashingSetting.Interpret(null));
        Assert.True(TaskbarFlashingSetting.Interpret(1));
        Assert.False(TaskbarFlashingSetting.Interpret(0));
    }

    [Fact]
    public void Its_Colour_Follows_The_Spec_Table()
    {
        Color accent = Color.FromRgb(0x20, 0x60, 0x70);

        Assert.Equal(Color.FromRgb(0xB3, 0xB3, 0xB3), AttentionBrushConverter.ColourFor(0.0, AttentionState.Pulsing, accent));
        Assert.Equal(Color.FromRgb(0xB3, 0xB3, 0xB3), AttentionBrushConverter.ColourFor(1.0, AttentionState.Pulsing, accent));
        Assert.Equal(Color.FromRgb(0x80, 0x80, 0x80), AttentionBrushConverter.ColourFor(0.0, AttentionState.Decayed, accent));
        Assert.Equal(accent, AttentionBrushConverter.ColourFor(2.0, AttentionState.Pulsing, accent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_Drawn_Animation_Has_The_Published_Duty(bool decayed)
    {
        // Integrates the key frames WPF will actually play, not the Core function they came from.
        DoubleAnimationUsingKeyFrames animation = AttentionBadge.Build(decayed);
        double cycle = animation.Duration.TimeSpan.TotalSeconds;

        double lit = 0;
        for (int i = 1; i < animation.KeyFrames.Count; i++)
        {
            DoubleKeyFrame a = animation.KeyFrames[i - 1], b = animation.KeyFrames[i];
            double span = b.KeyTime.TimeSpan.TotalSeconds - a.KeyTime.TimeSpan.TotalSeconds;
            lit += (a.Value + b.Value) / 2 * span;
        }

        double expected = decayed ? NotificationBadge.DecayedPulseDuty : NotificationBadge.PulseDuty;
        Assert.Equal(NotificationBadge.CycleSeconds(decayed), cycle, 6);
        Assert.Equal(expected, lit / cycle, 6);
        Assert.Equal(0.0, animation.KeyFrames[0].Value);
    }

    [Fact]
    public void Nothing_Asking_Means_No_Animation()
    {
        Assert.Null(AttentionBadge.PulseFor(AttentionState.None));
        Assert.NotNull(AttentionBadge.PulseFor(AttentionState.Pulsing));
        Assert.NotSame(AttentionBadge.PulseFor(AttentionState.Pulsing), AttentionBadge.PulseFor(AttentionState.Decayed));
    }
}
