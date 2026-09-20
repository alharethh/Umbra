using Umbra.Core.Layout;

namespace Umbra.Shell.Tests;

/// <summary>
/// The notification area is drawn larger than §7.1 specifies, and the pitch had to move with it.
/// </summary>
/// <remarks>
/// <para>
/// §7.1 puts a 16 px icon in a 24 px slot, which leaves a 4 px gutter — and §6.2 derives drift
/// amplitude from that gutter. Growing the glyph alone shrinks the gutter while every amplitude
/// stays where it was, so the tray icons leave their slots at the drift turnarounds and P5 is
/// breached: silently, only at the extremes of a cycle minutes long, and only for the icons at the
/// ends of the group.
/// </para>
/// <para>
/// This is the icon row's growth trap arriving from the other direction — there the icon grows on
/// hover and <c>MotionDriver.Reemit</c> guards it. Here nothing grows at runtime, so the guard is
/// the geometry itself: move the pitch by exactly what the glyph gained.
/// </para>
/// </remarks>
public class TrayGlyphSizeTests
{
    private static SlotMetrics Drawn() =>
        new(BarWindow.TrayPitchExtent, BarWindow.TrayGlyphExtent);

    private static SlotMetrics Spec() =>
        new(BarMetrics.BaseTrayPitch, BarMetrics.BaseTrayIconExtent);

    /// <summary>The glyph really is larger than the spec's, or none of this was worth doing.</summary>
    [Fact]
    public void The_Tray_Is_Drawn_Larger_Than_The_Spec() =>
        Assert.True(
            BarWindow.TrayGlyphExtent > BarMetrics.BaseTrayIconExtent,
            $"{BarWindow.TrayGlyphExtent} px is not larger than §7.1's {BarMetrics.BaseTrayIconExtent} px");

    /// <summary>And it still fits inside its slot while drifting (P5).</summary>
    [Fact]
    public void The_Enlarged_Glyph_Stays_Inside_Its_Slot() =>
        Assert.True(
            Drawn().SatisfiesSlotContainment,
            $"peak offset {Drawn().PeakOffset:F2} px against a {Drawn().Gutter:F2} px gutter");

    /// <summary>
    /// The gutter is untouched, which is what makes every derived amplitude still correct.
    /// </summary>
    [Fact]
    public void The_Gutter_Is_Exactly_The_Spec_Gutter() =>
        Assert.Equal(Spec().Gutter, Drawn().Gutter, 6);

    /// <summary>
    /// The mistake this exists to catch: applying the spec's drift ratio to a glyph that is not
    /// the spec's size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Containment on its own cannot catch it.</b> §6.2 derives amplitude from the gutter, so a
    /// cramped slot politely asks for less travel and still fits — the first version of this test
    /// asserted that and passed against the broken arrangement. What actually breaks P5 is taking
    /// the amplitude from a slot we are not drawing.
    /// </para>
    /// <para>
    /// <c>BarMetrics.TrayDriftRatio</c> is computed from §7.1's 16-in-24. Applied to a 20 px glyph
    /// left in a 24 px pitch it puts more travel into the slot than the gutter has — silently, only
    /// at the turnarounds of a cycle minutes long, and only for the icons at the ends of the group.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_Specs_Drift_Ratio_Would_Overrun_An_Enlarged_Glyph()
    {
        BarMetrics metrics = new(BarEdge.Bottom, Alignment.Start, scale: 1.0);

        // What the bar would apply if it kept reading the spec's ratio.
        double travel = metrics.Slot.PeakOffset * metrics.TrayDriftRatio;

        SlotMetrics cramped = new(BarMetrics.BaseTrayPitch, BarWindow.TrayGlyphExtent);

        Assert.True(
            travel > cramped.Gutter,
            $"the premise no longer holds: {travel:F2} px of travel fits the {cramped.Gutter:F2} px " +
            "gutter an enlarged glyph leaves in the spec's pitch");

        // With the pitch moved to match, the same travel is comfortably inside.
        Assert.True(
            travel < Drawn().Gutter,
            $"{travel:F2} px of travel does not fit the {Drawn().Gutter:F2} px gutter we actually draw");
    }

    /// <summary>
    /// The amplitude the bar applies comes from the slot the bar draws.
    /// </summary>
    /// <remarks>
    /// This is the assertion that actually guards the code. The test above states the hazard in
    /// numbers; this one fails if anyone points the ratio back at
    /// <c>BarMetrics.TrayDriftRatio</c>, which is derived from §7.1's 16-in-24 and is a different
    /// number the moment the drawn slot is not the spec's.
    /// </remarks>
    [Fact]
    public void The_Drift_Ratio_Comes_From_The_Drawn_Slot()
    {
        BarMetrics metrics = new(BarEdge.Bottom, Alignment.Start, scale: 1.0);

        double expected = Drawn().MaxAmplitude / metrics.Slot.MaxAmplitude;

        Assert.Equal(expected, BarWindow.TrayDriftRatioFor(metrics), 9);
        Assert.NotEqual(metrics.TrayDriftRatio, BarWindow.TrayDriftRatioFor(metrics), 9);
    }
}
