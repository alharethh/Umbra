using Umbra.Core.Layout;
using Umbra.Core.Tiers;
using Umbra.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// The Start glyph shrinks while dormant, by the same proportion as the icons.
/// </summary>
public class StartGlyphScaleTests
{
    private const double Full = BarWindow.ActiveIconExtent;

    [Fact]
    public void Start_Is_Unchanged_Once_The_Bar_Is_Engaged()
    {
        Assert.Equal(1.0, BarWindow.StartGlyphScaleFor(Full, 1.0), 6);
        Assert.Equal(1.0, BarWindow.StartGlyphScaleFor(Full * 1.25, 1.25), 6);
    }

    /// <summary>The default 16 px dormant icon shrinks Start to 16/26 of its size.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Start_Shrinks_In_Proportion_To_The_Icons(double scale)
    {
        Assert.Equal(16.0 / Full, BarWindow.StartGlyphScaleFor(16.0 * scale, scale), 6);
    }

    /// <summary>
    /// Never above 1 anywhere on the ramp, so the glyph is never drawn larger than the full-size
    /// one <c>ContentOffset</c>'s amplitude already allowed for, and P5 cannot be broken by this.
    /// </summary>
    [Theory]
    [InlineData(24.0)]
    [InlineData(16.0)]
    [InlineData(12.0)]
    public void Start_Never_Grows_Past_Its_Full_Size(double dormant)
    {
        double previous = 0.0;
        for (double level = 0.0; level <= 2.0; level += 0.01)
        {
            double blend = TierPalette.Resolve(BarElement.RunningIcon, level).ChromaBlend;
            double factor = BarWindow.StartGlyphScaleFor(IconSizeRamp.Extent(dormant, Full, blend), 1.0);

            Assert.InRange(factor, 0.0, 1.0);
            Assert.True(factor >= previous - 1e-9, $"scale fell from {previous} to {factor} at tier {level:F2}");
            previous = factor;
        }
    }

    /// <summary>
    /// With the row at the screen edge, Start is the first slot and nothing holds it off the corner
    /// but P5. It rides the icon's offset, so it must never be drawn wider than the icon is.
    /// </summary>
    [Theory]
    [InlineData(24.0)]
    [InlineData(16.0)]
    [InlineData(12.0)]
    public void Start_Is_Never_Wider_Than_The_Icon_Whose_Drift_It_Carries(double dormant)
    {
        for (double level = 0.0; level <= 2.0; level += 0.01)
        {
            double blend = TierPalette.Resolve(BarElement.RunningIcon, level).ChromaBlend;
            double icon = IconSizeRamp.Extent(dormant, Full, blend);
            double start = BarWindow.StartGlyphExtent * BarWindow.StartGlyphScaleFor(icon, 1.0);

            var slot = new SlotMetrics(SlotMetrics.BasePitch, icon);
            Assert.True(start <= icon, $"Start {start:F2} wider than icon {icon:F2} at tier {level:F2}");
            Assert.True(SlotMetrics.BasePitch / 2.0 - start / 2.0 - slot.PeakOffset >= 0.0,
                $"Start would clip at the corner at tier {level:F2}");
        }
    }
}
