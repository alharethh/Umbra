namespace Umbra.Core.Layout;

/// <summary>
/// Chooses the drawn icon extent for a tier, so the icon can be smaller while the bar is dormant
/// and full size once the user is looking at it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why shrink at all.</b> §6.2 derives drift amplitude from the gutter, and the gutter is what
/// is left of the pitch once the icon is drawn. A smaller icon therefore travels further, and every
/// screen pixel beneath it rests more. §6.0's own table is the warning attached to this: the payoff
/// is far smaller than the geometry suggests. In a 44 px slot the permanently-lit core vanishes at
/// 19.29 px, and at that size the hottest pixel is <i>still</i> covered 99.8% of the time — a
/// 1.00× improvement. Measured against the motion model, 18 px buys 1.04×, 16 px 1.25×, 14 px
/// 1.63×, 12 px 2.32×. The core disappearing is a geometric milestone that does not track wear and
/// must not be quoted as though it does.
/// </para>
/// <para>
/// <b>Why ramp it rather than just shrinking.</b> A permanently smaller icon is a poor trade, and
/// it is unnecessary. Under P3 the wear that matters accrues at Tier 0 and Tier 1, which together
/// run ~100% duty; Tier 2 is ~0.1%. Shrinking only while the bar is monochrome keeps essentially
/// the whole benefit and returns the whole visual cost. The ramp rides
/// <c>TierRender.ChromaBlend</c>, so the icon reaches full size exactly as it reaches full colour.
/// </para>
/// <para>
/// <b>The trap this exists to make testable.</b> A grown icon has a narrower gutter and so a
/// smaller permitted amplitude. Deriving amplitude from the dormant extent and then drawing a
/// larger icon pushes it outside its own slot at the extremes of the drift, which P5 forbids
/// outright — and it does so silently, only at the turnarounds, only once the dormant size is 18 px
/// or below. Amplitude must always be derived from the extent actually being drawn.
/// </para>
/// </remarks>
public static class IconSizeRamp
{
    /// <summary>
    /// The extent to draw at a given point on the dormant → full-size ramp.
    /// </summary>
    /// <param name="dormantExtent">Extent while the bar is monochrome.</param>
    /// <param name="fullExtent">Extent once the bar is in full chroma.</param>
    /// <param name="blend">
    /// Position on the ramp, 0 to 1 — <c>TierRender.ChromaBlend</c> at the current tier level.
    /// </param>
    /// <remarks>
    /// A dormant extent at or above the full extent means the feature is off, and the full extent
    /// is returned at every blend rather than ramping downward — shrinking on hover would be the
    /// exact opposite of the intent.
    /// </remarks>
    public static double Extent(double dormantExtent, double fullExtent, double blend)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dormantExtent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fullExtent);

        if (dormantExtent >= fullExtent) return fullExtent;

        blend = Math.Clamp(blend, 0.0, 1.0);
        return dormantExtent + (fullExtent - dormantExtent) * blend;
    }

    /// <summary>
    /// Whether an icon of this extent, drifting by the amplitude its own gutter allows, stays
    /// inside its hit slot (P5).
    /// </summary>
    public static bool StaysInSlot(double pitch, double extent)
    {
        if (extent <= 0 || extent > pitch) return false;

        var slot = new SlotMetrics(pitch, extent);
        return slot.SatisfiesSlotContainment
            && slot.PeakOffset + extent / 2.0 <= pitch / 2.0;
    }
}
