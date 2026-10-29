using Umbra.Core.Color;
using Umbra.Core.Tiers;
using Umbra.Core.Wear;

namespace Umbra.Core.Tests;

/// <summary>
/// The user's resting brightness: Tiers 0 and 1 scaled in linear light, Tier 2 and the badge left
/// alone, never brighter than the table.
/// </summary>
public class RestingBrightnessTests
{
    private static readonly BarElement[] Elements =
    [
        BarElement.PinnedIcon, BarElement.RunningIcon, BarElement.RunningIndicator,
        BarElement.Clock, BarElement.StartGlyph, BarElement.TrayIcon, BarElement.NotificationBadge,
    ];

    private static double L(Rgb colour) => Srgb.RelativeLuminance(colour);

    [Fact]
    public void Full_Brightness_Draws_Exactly_The_Table()
    {
        // The default must be byte-identical to before the setting existed: every published wear
        // figure was measured against it.
        foreach (BarElement element in Elements)
            for (double level = 0; level <= 2.0; level += 0.0625)
                Assert.Equal(TierPalette.Resolve(element, level), TierPalette.Resolve(element, level, 1.0));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.25)]
    [InlineData(TierPalette.MinRestingBrightness)]
    public void The_Resting_Tiers_Carry_That_Fraction_Of_Their_Light(double brightness)
    {
        foreach (BarElement element in Elements.Where(e => e != BarElement.NotificationBadge))
        foreach (double level in (double[])[0.0, 1.0])
        {
            Rgb table = TierPalette.Resolve(element, level).Monochrome;
            if (table == Rgb.Black) continue; // the dormant indicator is not drawn at all

            double expected = L(table) * brightness;
            double drawn = L(TierPalette.Resolve(element, level, brightness).Monochrome);

            // One 8-bit step of #0B0B0B is about 9% of its own light, so the tolerance is relative
            // to the step, not to the value.
            Assert.InRange(drawn, expected * 0.85, expected * 1.15);
        }
    }

    [Fact]
    public void Half_The_Light_Is_About_A_Third_Of_The_Wear()
    {
        Rgb table = TierPalette.Resolve(BarElement.PinnedIcon, 0.0).Monochrome;
        Rgb dim = TierPalette.Resolve(BarElement.PinnedIcon, 0.0, 0.5).Monochrome;

        double ratio = WearModel.Rate(table) / WearModel.Rate(dim);

        Assert.InRange(ratio, 2.9, 3.6);
    }

    [Fact]
    public void Tier_Two_Is_Untouched()
    {
        foreach (BarElement element in Elements)
        {
            TierRender table = TierPalette.Resolve(element, 2.0);
            TierRender dim = TierPalette.Resolve(element, 2.0, TierPalette.MinRestingBrightness);

            Assert.Equal(table.ChromaBlend, dim.ChromaBlend);
            Assert.Equal(table.UsesSystemAccent, dim.UsesSystemAccent);

            // Fully blended to the artwork or the accent, the grey it started from is not drawn.
            if (table.ChromaBlend < 1.0) Assert.Equal(table.Monochrome, dim.Monochrome);
        }
    }

    [Fact]
    public void The_Ramp_Into_Tier_Two_Starts_From_The_Dimmed_Tier_One_And_Ends_At_The_Table()
    {
        // The clock is flat at Tier 2, so its 1 → 2 ramp is a colour lerp rather than a chroma
        // blend: the far end must not be dimmed, or the engaged clock would be too.
        Rgb engaged = TierPalette.Resolve(BarElement.Clock, 2.0).Monochrome;
        Rgb dimAmbient = TierPalette.Resolve(BarElement.Clock, 1.0, 0.25).Monochrome;
        Rgb halfway = TierPalette.Resolve(BarElement.Clock, 1.5, 0.25).Monochrome;

        Assert.Equal(Srgb.Lerp(dimAmbient, engaged, 0.5), halfway);
    }

    [Fact]
    public void An_Icon_Fading_To_Colour_Starts_From_The_Dimmed_Grey()
    {
        TierRender halfway = TierPalette.Resolve(BarElement.PinnedIcon, 1.5, 0.25);

        Assert.Equal(TierPalette.Resolve(BarElement.PinnedIcon, 1.0, 0.25).Monochrome, halfway.Monochrome);
        Assert.Equal(0.5, halfway.ChromaBlend);
    }

    [Fact]
    public void It_Can_Never_Make_The_Bar_Brighter_Or_Black()
    {
        foreach (BarElement element in Elements)
        foreach (double level in (double[])[0.0, 0.5, 1.0])
        {
            Assert.Equal(TierPalette.Resolve(element, level), TierPalette.Resolve(element, level, 3.0));
            Assert.Equal(
                TierPalette.Resolve(element, level, TierPalette.MinRestingBrightness),
                TierPalette.Resolve(element, level, 0.0));
        }
    }

    [Fact]
    public void The_Attention_Badge_Is_Not_Dimmed()
    {
        foreach (double level in (double[])[0.0, 1.0])
            Assert.Equal(
                TierPalette.Resolve(BarElement.NotificationBadge, level),
                TierPalette.Resolve(BarElement.NotificationBadge, level, TierPalette.MinRestingBrightness));
    }
}
