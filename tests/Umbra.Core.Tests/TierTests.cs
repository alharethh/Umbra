using Umbra.Core.Tiers;
using Umbra.Core.Wear;

namespace Umbra.Core.Tests;

/// <summary>Design-spec §3 — tier values, the proximity ramp, and the notification badge.</summary>
public class TierTests
{
    [Theory]
    [InlineData(BarElement.PinnedIcon, Tier.Dormant, "#333333")]
    [InlineData(BarElement.PinnedIcon, Tier.Ambient, "#404040")]
    [InlineData(BarElement.RunningIcon, Tier.Dormant, "#404040")]
    [InlineData(BarElement.RunningIcon, Tier.Ambient, "#4D4D4D")]
    [InlineData(BarElement.RunningIndicator, Tier.Ambient, "#404040")]
    [InlineData(BarElement.RunningIndicator, Tier.Engaged, "#666666")]
    [InlineData(BarElement.Clock, Tier.Dormant, "#333333")]
    [InlineData(BarElement.Clock, Tier.Ambient, "#404040")]
    [InlineData(BarElement.Clock, Tier.Engaged, "#666666")]
    [InlineData(BarElement.StartGlyph, Tier.Dormant, "#333333")]
    [InlineData(BarElement.TrayIcon, Tier.Dormant, "#333333")]
    [InlineData(BarElement.NotificationBadge, Tier.Dormant, "#B3B3B3")]
    public void Section31_Table_Reproduces(BarElement element, Tier tier, string hex)
    {
        TierAppearance appearance = TierPalette.Appearance(element, tier);

        Assert.Equal(Rgb.FromHex(hex), appearance.Colour);
    }

    [Fact]
    public void Background_Is_Exactly_Black_At_Every_Tier()
    {
        // §4: #000000 exactly. Not #010101. Not near-black. A black pixel draws no current, and
        // every non-black pixel is a cost that has to justify itself.
        foreach (Tier tier in Enum.GetValues<Tier>())
        {
            TierAppearance background = TierPalette.Appearance(BarElement.Background, tier);

            Assert.Equal(Rgb.Black, background.Colour);
            Assert.Equal(0.0, Srgb.RelativeLuminance(background.Colour));
            Assert.Equal(0.0, WearModel.Rate(background.Colour));
        }
    }

    [Fact]
    public void Running_Indicator_Is_Not_Drawn_At_All_When_Dormant()
    {
        Assert.Equal(TierAppearanceKind.Hidden,
            TierPalette.Appearance(BarElement.RunningIndicator, Tier.Dormant).Kind);
    }

    [Fact]
    public void Icons_Are_Full_Chroma_Only_At_Tier_2()
    {
        foreach (BarElement element in new[] { BarElement.PinnedIcon, BarElement.RunningIcon, BarElement.TrayIcon })
        {
            Assert.Equal(TierAppearanceKind.Solid, TierPalette.Appearance(element, Tier.Dormant).Kind);
            Assert.Equal(TierAppearanceKind.Solid, TierPalette.Appearance(element, Tier.Ambient).Kind);
            Assert.Equal(TierAppearanceKind.FullChromaCapped, TierPalette.Appearance(element, Tier.Engaged).Kind);
        }
    }

    [Fact]
    public void Tier1_Sits_One_Step_Darker_Than_Tier1_Of_V1()
    {
        // §2.2's consequence: Tier 1 was a third to a half of the whole budget, so it was darkened
        // one step throughout. v1's Tier 1 for a pinned icon was #4D4D4D.
        double v2 = WearModel.Rate(TierPalette.Appearance(BarElement.PinnedIcon, Tier.Ambient).Colour);
        double v1 = WearModel.Rate(Rgb.FromHex("#4D4D4D"));

        Assert.True(v2 < v1);
        Approx.Relative(1.875, v1 / v2, 0.01, "one step of darkening, in wear terms");
    }

    [Fact]
    public void Tier2_Greys_Dropped_From_808080_To_666666()
    {
        // §3.1: against a pure black background, 26.6 nits is emphatically legible.
        Rgb tier2 = TierPalette.Appearance(BarElement.Clock, Tier.Engaged).Colour;

        Assert.Equal(Rgb.FromHex("#666666"), tier2);
        Assert.True(WearModel.Rate(tier2) < WearModel.Rate(Rgb.FromHex("#808080")));
    }

    [Fact]
    public void Proximity_Ramp_Is_Continuous_Across_The_Band()
    {
        TierController controller = new();

        Assert.Equal(0.0, controller.TargetLevel(200.0));
        Assert.Equal(0.0, controller.TargetLevel(120.0));
        Approx.Equal(0.5, controller.TargetLevel(80.0), 1e-12, "midpoint of the ramp");
        Approx.Equal(0.9875, controller.TargetLevel(41.0), 1e-12, "just outside the engaged band");
        Assert.Equal(2.0, controller.TargetLevel(10.0));

        // The ramp tops out at Tier 1 exactly where the engaged band begins; crossing into it is
        // the one step in the whole curve, and the dwell guard is what gates that step.
        Approx.Equal(1.0, controller.TargetLevel(40.0, dwellSatisfied: false), 1e-12, "top of the ramp");
    }

    [Fact]
    public void Proximity_Band_Scales_With_Dpi()
    {
        // §3's correction: v1 specified 200 px in raw pixels, which both inflated Tier 1 duty and
        // covered a different fraction of the screen on every monitor.
        TierController at100 = new(1.0);
        TierController at150 = new(1.5);

        Assert.Equal(120.0, at100.AmbientBand);
        Assert.Equal(180.0, at150.AmbientBand);
        Assert.Equal(60.0, at150.EngagedBand);

        // The same physical distance sits at different tiers on different monitors — as it should.
        Assert.Equal(0.0, at100.TargetLevel(150.0));
        Assert.True(at150.TargetLevel(150.0) > 0.0);
    }

    [Fact]
    public void Dwell_Guard_Blocks_A_Pointer_Merely_Crossing_The_Bar()
    {
        // §3.4: do not promote to Tier 2 until the pointer has been inside 40 px for 60 ms.
        TierController controller = new();

        controller.Update(distance: 10.0, seconds: 0.030);
        Assert.True(controller.Level < 1.0, "Nothing promotes to Tier 2 within the dwell window.");
        Assert.Equal(1.0, controller.TargetLevel(10.0, dwellSatisfied: false));

        controller.Update(distance: 10.0, seconds: 0.040);
        Assert.True(controller.DwellSecondsElapsed >= TierController.DwellSeconds);
        Assert.Equal(2.0, controller.TargetLevel(10.0, dwellSatisfied: true));
    }

    [Fact]
    public void Dwell_Resets_When_The_Pointer_Leaves()
    {
        TierController controller = new();

        controller.Update(10.0, 0.050);
        controller.Update(300.0, 0.010);

        Assert.Equal(0.0, controller.DwellSecondsElapsed);
    }

    [Fact]
    public void Attack_Is_Faster_Than_Release()
    {
        // §3.4: release is deliberately slower so the bar does not flicker during transient
        // pointer passes.
        Assert.Equal(0.120, TierController.AttackSeconds);
        Assert.Equal(0.400, TierController.ReleaseSeconds);

        TierController rising = new();
        rising.Reset(0.0);
        rising.Update(distance: 60.0, seconds: 0.060);       // half of the attack time

        TierController falling = new();
        falling.Reset(1.0);
        falling.Update(distance: 300.0, seconds: 0.060);     // and of the release

        Assert.True(rising.Level > 1.0 - falling.Level,
            "One attack time-constant must move further than one release time-constant.");
    }

    [Fact]
    public void Level_Reaches_Tier1_In_One_Attack_Constant()
    {
        TierController controller = new();

        controller.Update(distance: 41.0, seconds: TierController.AttackSeconds);

        Approx.Relative(controller.TargetLevel(41.0), controller.Level, 0.02, "level after one attack constant");
    }

    [Fact]
    public void Keyboard_Focus_Promotes_With_No_Pointer_Anywhere_Near()
    {
        // §3: Win+T and arrow navigation have no pointer path at all.
        TierController controller = new();

        Assert.Equal(2.0, controller.TargetLevel(5000.0, keyboardFocus: true));
    }

    [Fact]
    public void Ramp_Interpolates_In_Linear_Light()
    {
        // §3.4, and one of the failures that looks fine and is not: interpolating in sRGB values
        // makes the ramp spend more time bright than intended.
        //
        // Measured on the running indicator, which ramps from hidden (black) to #404040. The
        // adjacent greys elsewhere in §3.1 are too close together to tell the two methods apart —
        // #333333 to #404040 puts both midpoints inside the same 8-bit step, so a test written
        // against that pair passes whichever way the interpolation is done.
        TierRender midpoint = TierPalette.Resolve(BarElement.RunningIndicator, 0.5);

        Rgb dormant = TierPalette.Appearance(BarElement.RunningIndicator, Tier.Dormant).EffectiveColour;
        Rgb ambient = TierPalette.Appearance(BarElement.RunningIndicator, Tier.Ambient).Colour;

        double expected = (Srgb.RelativeLuminance(dormant) + Srgb.RelativeLuminance(ambient)) / 2.0;
        Approx.Relative(expected, Srgb.RelativeLuminance(midpoint.Monochrome), 0.02,
            "half-way up the ramp is half-way in light");

        // The naive version — averaging the encoded bytes — lands at barely half the luminance.
        Rgb naive = new(
            (byte)((dormant.R + ambient.R) / 2),
            (byte)((dormant.G + ambient.G) / 2),
            (byte)((dormant.B + ambient.B) / 2));

        Assert.NotEqual(naive, midpoint.Monochrome);
        Assert.True(Srgb.RelativeLuminance(midpoint.Monochrome) > Srgb.RelativeLuminance(naive) * 1.5,
            "Interpolating encoded values undershoots the intended midpoint substantially.");
    }

    [Fact]
    public void Hidden_Elements_Fade_Up_From_Black()
    {
        TierRender dormant = TierPalette.Resolve(BarElement.RunningIndicator, 0.0);
        TierRender midway = TierPalette.Resolve(BarElement.RunningIndicator, 0.5);
        TierRender ambient = TierPalette.Resolve(BarElement.RunningIndicator, 1.0);

        Assert.True(dormant.IsDark);
        Assert.True(Srgb.RelativeLuminance(midway.Monochrome) > 0.0);
        Assert.True(Srgb.RelativeLuminance(midway.Monochrome) < Srgb.RelativeLuminance(ambient.Monochrome));
    }

    [Fact]
    public void Chroma_Cross_Fades_Above_Tier_1()
    {
        // There is no colour to interpolate toward, so the monochrome value holds at its Tier 1
        // setting and the capped artwork fades in over it.
        TierRender ambient = TierPalette.Resolve(BarElement.PinnedIcon, 1.0);
        TierRender halfway = TierPalette.Resolve(BarElement.PinnedIcon, 1.5);
        TierRender engaged = TierPalette.Resolve(BarElement.PinnedIcon, 2.0);

        Assert.Equal(0.0, ambient.ChromaBlend);
        Approx.Equal(0.5, halfway.ChromaBlend, 1e-12, "chroma blend at the midpoint");
        Assert.Equal(1.0, engaged.ChromaBlend);
        Assert.Equal(Rgb.FromHex("#404040"), engaged.Monochrome);
    }

    [Fact]
    public void Badge_Pulses_Rather_Than_Holding()
    {
        // §3.3: 1 s on, 3 s off. This quarters duty and reads as more attention-grabbing, not less.
        Approx.Equal(0.25, NotificationBadge.PulseDuty, 1e-12, "badge pulse duty");
        Assert.Equal(0.200, NotificationBadge.FadeSeconds);

        double held = WearModel.Rate(NotificationBadge.Colour);
        double pulsed = NotificationBadge.WearRate(0.0);

        Approx.Relative(4.0, held / pulsed, 1e-9, "pulsing quarters the badge's wear");
    }

    [Fact]
    public void Badge_Decays_When_Ignored()
    {
        // §3.3: after 15 minutes unacknowledged, step down to #808080 and lengthen the pulse.
        // A badge the user has demonstrably chosen not to act on does not need 90 nits.
        Assert.False(NotificationBadge.HasDecayed(899.0));
        Assert.True(NotificationBadge.HasDecayed(900.0));

        Assert.Equal(Rgb.FromHex("#B3B3B3"), NotificationBadge.ColourAt(0.0));
        Assert.Equal(Rgb.FromHex("#808080"), NotificationBadge.ColourAt(1000.0));

        Approx.Relative(1.0 / 7.0, NotificationBadge.DecayedPulseDuty, 1e-12, "decayed pulse duty");

        double before = NotificationBadge.WearRate(0.0);
        double after = NotificationBadge.WearRate(1000.0);
        Assert.True(after < before / 5.0, $"Decay must cut badge wear substantially ({before / after:F1}×).");
    }

    [Fact]
    public void Badge_Is_The_Brightest_Static_Element_In_The_Design()
    {
        // Which is why it is the one element that gets its own mitigation section.
        double badge = Srgb.RelativeLuminance(NotificationBadge.Colour);

        foreach (BarElement element in Enum.GetValues<BarElement>())
        {
            if (element == BarElement.NotificationBadge) continue;

            TierAppearance rest = TierPalette.Appearance(element, Tier.Ambient);
            if (rest.Kind != TierAppearanceKind.Solid) continue;

            Assert.True(Srgb.RelativeLuminance(rest.Colour) < badge);
        }
    }

    /// <summary>
    /// With the engaged band at zero the bar lights up only when the pointer is actually on it.
    /// </summary>
    /// <remarks>
    /// §3 specifies a 40 px band, which in use reads as the bar reacting to traffic passing by
    /// rather than to the user arriving. Engagement on contact is a deliberate choice. What
    /// must survive that change is the Tier 0 → 1 ambient ramp: it is the near-invisible grey step
    /// §2.3's duty budget is built on, and collapsing it would quietly move the whole bar between
    /// two states instead of three.
    /// </remarks>
    [Fact]
    public void A_Zero_Engaged_Band_Engages_Only_On_Contact()
    {
        TierController controller = new(engagedBandPixels: 0.0);

        Assert.Equal(0.0, controller.EngagedBand);

        // On the bar.
        Approx.Equal(2.0, controller.TargetLevel(0.0), 1e-9, "over the bar");

        // One pixel away is emphatically not engaged.
        Assert.True(controller.TargetLevel(1.0) < 1.01,
            "A pointer beside the bar must not reach the engaged state.");

        // The ambient ramp is untouched: still zero at the band edge, still rising on approach.
        Approx.Equal(0.0, controller.TargetLevel(TierPalette.AmbientBandPixels), 1e-9, "band edge");
        Assert.True(controller.TargetLevel(60.0) > 0.0 && controller.TargetLevel(60.0) < 1.0,
            "Mid-band must still be a partial ambient level.");

        // And it is monotonic across the approach, so nothing snaps on the way in.
        double previous = -1.0;
        for (double d = TierPalette.AmbientBandPixels; d >= 1.0; d -= 1.0)
        {
            double level = controller.TargetLevel(d);
            Assert.True(level >= previous, $"Level went backwards approaching at {d} px.");
            previous = level;
        }
    }

    [Fact]
    public void The_Default_Engaged_Band_Is_Still_The_Spec_Figure()
    {
        // The override is opt-in; nothing else in the library changes behaviour.
        Assert.Equal(TierPalette.EngagedBandPixels, new TierController().EngagedBand);
    }

}
