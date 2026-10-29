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
    private static readonly IntPtr A = 0x10, B = 0x20;
    private static readonly IntPtr[] None = [], OnlyA = [A], Both = [A, B];

    private static AttentionState Step(AttentionLatch latch, IntPtr[] flashing, bool active, double now,
        IntPtr[]? open = null) =>
        latch.Update(open ?? Both, flashing, active, now);

    [Fact]
    public void A_Flash_Raises_The_Badge_And_Switching_To_It_Clears_It()
    {
        AttentionLatch latch = new();

        Assert.Equal(AttentionState.None, Step(latch, None, false, 0));
        Assert.Equal(AttentionState.Pulsing, Step(latch, OnlyA, false, 1));
        Assert.Equal(AttentionState.None, Step(latch, OnlyA, true, 2));
    }

    [Fact]
    public void A_Window_That_Flashes_Forever_Holds_One_Request_Through_The_Gaps()
    {
        // FlashWindowEx toggles at the caret-blink rate and ManagedShell follows every toggle:
        // HSHELL_FLASH sets Flashing, the HSHELL_REDRAW that turns it off sets Inactive. Fed that,
        // the badge must stay up, never restart, and decay on schedule.
        AttentionLatch latch = new();
        const double half = 0.53;

        double t = 100;
        for (int i = 0; t < 100 + NotificationBadge.DecayAfterSeconds - 1; i++, t = 100 + i * half)
            Assert.Equal(AttentionState.Pulsing, Step(latch, i % 2 == 0 ? OnlyA : None, false, t));

        Assert.Equal(100 + NotificationBadge.DecayAfterSeconds, latch.DecaysAt);
        Assert.Equal(AttentionState.Decayed, Step(latch, None, false, 100 + NotificationBadge.DecayAfterSeconds));
        Assert.Equal(AttentionState.Decayed, Step(latch, OnlyA, false, 100 + NotificationBadge.DecayAfterSeconds + half));
    }

    [Fact]
    public void Being_Switched_To_Holds_While_The_Same_Request_Keeps_Blinking()
    {
        // FLASHW_TIMER does not stop on activation, and another window of the application may still
        // be asking. Neither is a new request: acknowledged is acknowledged through the gaps.
        AttentionLatch latch = new();

        Step(latch, Both, false, 0);
        Assert.Equal(AttentionState.None, Step(latch, None, true, 0.5));

        for (double t = 1; t < 60; t += 0.53)
            Assert.Equal(AttentionState.None, Step(latch, (int)(t / 0.53) % 2 == 0 ? OnlyA : None, false, t));
    }

    [Fact]
    public void A_Flash_After_A_Quiet_Spell_Is_A_New_Request()
    {
        AttentionLatch latch = new();

        Step(latch, OnlyA, false, 0);
        Step(latch, OnlyA, true, 1);
        Step(latch, None, false, 2);

        Assert.Equal(AttentionState.None, Step(latch, OnlyA, false, 1 + AttentionLatch.QuietSeconds - 0.1));
        Assert.Equal(AttentionState.Pulsing, Step(latch, OnlyA, false, 1 + 2 * AttentionLatch.QuietSeconds + 0.5));
    }

    [Fact]
    public void Closing_Every_Window_That_Asked_Ends_The_Request()
    {
        AttentionLatch latch = new();

        Step(latch, OnlyA, false, 0);
        Assert.Equal(AttentionState.Pulsing, Step(latch, None, false, 1));

        // B never asked; A closing leaves nothing asking.
        Assert.Equal(AttentionState.None, Step(latch, None, false, 2, open: [B]));
        Assert.Null(latch.DecaysAt);
    }

    [Fact]
    public void Closing_One_Asking_Window_Keeps_The_Other()
    {
        AttentionLatch latch = new();

        Step(latch, Both, false, 0);
        Assert.Equal(AttentionState.Pulsing, Step(latch, None, false, 1, open: [B]));
    }

    [Fact]
    public void An_Ignored_Badge_Decays_After_Fifteen_Minutes()
    {
        // A counted flash ends with the button lit and stays that way until the user comes.
        AttentionLatch latch = new();

        Step(latch, OnlyA, false, 100);
        Assert.Equal(100 + NotificationBadge.DecayAfterSeconds, latch.DecaysAt);
        Assert.Equal(AttentionState.Pulsing, Step(latch, OnlyA, false, 100 + 899));
        Assert.Equal(AttentionState.Decayed, Step(latch, OnlyA, false, 100 + 900));
    }

    [Fact]
    public void A_Withdrawn_Flash_Ends_The_Request_After_The_Quiet_Spell()
    {
        // FLASHW_STOP: one HSHELL_REDRAW, and then nothing at all. Measured 2026-10-29.
        AttentionLatch latch = new();
        const double stop = 0.53;

        Step(latch, OnlyA, false, 0);
        Assert.Equal(AttentionState.Pulsing, Step(latch, None, false, stop));
        Assert.Equal(stop + AttentionLatch.QuietSeconds, latch.WithdrawsAt);

        Assert.Equal(AttentionState.Pulsing, Step(latch, None, false, stop + AttentionLatch.QuietSeconds - 0.01));
        Assert.Equal(AttentionState.None, Step(latch, None, false, stop + AttentionLatch.QuietSeconds + 0.01));
        Assert.Null(latch.WithdrawsAt);
        Assert.Null(latch.DecaysAt);
    }

    [Fact]
    public void A_Counted_Flash_Left_Lit_Holds_And_Has_Nothing_To_Withdraw()
    {
        AttentionLatch latch = new();

        for (int i = 0; i < 10; i++)
            Step(latch, i % 2 == 0 ? OnlyA : None, false, i * 0.53);

        // The last toggle turned the button on, and it stays on.
        Assert.Equal(AttentionState.Pulsing, Step(latch, OnlyA, false, 9 * 0.53 + 600));
        Assert.Null(latch.WithdrawsAt);
    }

    [Fact]
    public void A_Lit_Window_Flashing_Again_After_A_Long_Silence_Keeps_Its_Request()
    {
        // A fresh FlashWindowEx on a window a counted flash left lit opens with an off-toggle. That
        // window was lit the whole time, so the quiet spell starts now, not when it was last heard.
        AttentionLatch latch = new();

        Step(latch, OnlyA, false, 0);
        Assert.Equal(AttentionState.Pulsing, Step(latch, None, false, 600));
        Assert.Equal(600 + AttentionLatch.QuietSeconds, latch.WithdrawsAt);
        Assert.Equal(AttentionState.Pulsing, Step(latch, OnlyA, false, 600.53));
        Assert.Equal(0 + NotificationBadge.DecayAfterSeconds, latch.DecaysAt);
    }

    [Fact]
    public void The_Pulse_Is_Not_Drawn_At_Sixty_Frames_A_Second()
    {
        Assert.Equal(AttentionBadge.FramesPerSecond, Timeline.GetDesiredFrameRate(AttentionBadge.Build(false)));
        Assert.Equal(AttentionBadge.FramesPerSecond, Timeline.GetDesiredFrameRate(AttentionBadge.Build(true)));
        Assert.InRange(AttentionBadge.FramesPerSecond, 1, 30);
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
