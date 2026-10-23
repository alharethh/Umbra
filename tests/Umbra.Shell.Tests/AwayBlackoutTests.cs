using System.IO;
using Umbra.Core.Color;
using Umbra.Shell.Rendering;
using Umbra.Shell.Settings;

namespace Umbra.Shell.Tests;

/// <summary>
/// The bar going black while nobody is there: when it does, what keeps it lit, and how fast it
/// comes back.
/// </summary>
/// <remarks>
/// Idle time, clock and playback are all injected, so the rule is driven exactly as the proximity
/// tick drives it, without a mouse or a sound card.
/// </remarks>
public class AwayBlackoutTests
{
    private const double Delay = 600;

    private sealed class Playback
    {
        public bool Playing;
        public int Asked;
        public bool Ask() { Asked++; return Playing; }
    }

    private static (AwayBlackout Away, Playback Playback) Make(bool playing = false)
    {
        Playback playback = new() { Playing = playing };
        return (new AwayBlackout(playback.Ask) { DelaySeconds = Delay }, playback);
    }

    [Fact]
    public void Goes_Black_Only_Once_The_Delay_Has_Run_Out()
    {
        (AwayBlackout away, _) = Make();

        Assert.False(away.Update(Delay - 0.1, 0));
        Assert.False(away.IsAway);

        Assert.True(away.Update(Delay, 0.1));
        Assert.True(away.IsAway);
    }

    [Fact]
    public void Comes_Back_On_The_First_Tick_After_Input()
    {
        (AwayBlackout away, _) = Make();
        away.Update(Delay + 300, 0);
        Assert.True(away.IsAway);

        Assert.True(away.Update(0.05, 0.1));
        Assert.False(away.IsAway);
    }

    [Fact]
    public void A_Playing_Video_Is_Not_Being_Away()
    {
        (AwayBlackout away, Playback playback) = Make(playing: true);

        for (double t = 0; t < 3600; t += 0.1)
            away.Update(Delay + t, t);

        Assert.False(away.IsAway);
        Assert.True(away.HeldByPlayback);

        // Asked every five seconds, not every tick: one Core Audio walk per 100 ms would be the
        // standing cost this is meant to avoid.
        Assert.InRange(playback.Asked, 3600 / (AwayBlackout.PlaybackRecheckSeconds + 0.2),
                                       3600 / AwayBlackout.PlaybackRecheckSeconds + 1);
    }

    [Fact]
    public void Goes_Black_Within_A_Recheck_Once_Playback_Ends()
    {
        (AwayBlackout away, Playback playback) = Make(playing: true);
        away.Update(Delay, 0);
        Assert.False(away.IsAway);

        playback.Playing = false;
        away.Update(Delay + 4.9, 4.9);
        Assert.False(away.IsAway);
        away.Update(Delay + 5.0, 5.0);
        Assert.True(away.IsAway);
    }

    [Fact]
    public void A_Playlist_Moving_On_Wakes_A_Black_Bar()
    {
        (AwayBlackout away, Playback playback) = Make();
        away.Update(Delay, 0);
        Assert.True(away.IsAway);

        playback.Playing = true;
        away.Update(Delay + 5, 5);
        Assert.False(away.IsAway);
    }

    [Fact]
    public void Playback_Is_Not_Asked_Before_The_Delay_Runs_Out()
    {
        (AwayBlackout away, Playback playback) = Make(playing: true);

        for (double t = 0; t < 60; t += 0.1) away.Update(t, t);

        Assert.Equal(0, playback.Asked);
    }

    [Fact]
    public void An_Old_Answer_Is_Not_Trusted_After_The_User_Came_Back()
    {
        (AwayBlackout away, Playback playback) = Make(playing: true);
        away.Update(Delay, 100);
        Assert.False(away.IsAway);

        // Input, then away again a second later by the clock: playback has stopped meanwhile, and
        // the stale "playing" from before must not hold the bar lit for the rest of a recheck.
        away.Update(0, 100.5);
        playback.Playing = false;
        away.Update(Delay, 101);

        Assert.True(away.IsAway);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Off_Never_Goes_Black(double delay)
    {
        (AwayBlackout away, _) = Make();
        away.DelaySeconds = delay;

        away.Update(86_400, 0);

        Assert.False(away.IsAway);
    }

    [Fact]
    public void The_Fade_Is_Linear_In_Light_Not_In_Opacity()
    {
        // Content over black: the light left is the linear-light value of opacity × sRGB value.
        // Halfway through the fade, half the light should be left — a linear opacity ramp leaves
        // about a fifth.
        foreach (double fraction in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 })
        {
            double opacity = AwayBlackout.OpacityForLight(fraction);
            double light = Srgb.ToLinear(opacity) / Srgb.ToLinear(1.0);
            Assert.InRange(light, fraction * 0.85, fraction * 1.15);
        }
    }

    [Fact]
    public void The_Setting_Defaults_On_At_Ten_Minutes_And_Round_Trips()
    {
        Assert.Equal(10.0, new UmbraSettings().AwayAfterMinutes);

        string path = Path.Combine(Path.GetTempPath(), $"umbra-away-{Guid.NewGuid():N}.txt");
        try
        {
            new UmbraSettings { AwayAfterMinutes = 0.25 }.Save(path);
            Assert.Equal(0.25, UmbraSettings.Load(path).AwayAfterMinutes);

            File.WriteAllLines(path, ["awayAfterMinutes = -3"]);
            Assert.Equal(10.0, UmbraSettings.Load(path).AwayAfterMinutes);

            File.WriteAllLines(path, ["awayAfterMinutes = soon"]);
            Assert.Equal(10.0, UmbraSettings.Load(path).AwayAfterMinutes);

            File.WriteAllLines(path, ["awayAfterMinutes = 0"]);
            Assert.Equal(0.0, UmbraSettings.Load(path).AwayAfterMinutes);
        }
        finally { File.Delete(path); }
    }
}
