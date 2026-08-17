namespace Umbra.Core.Layout;

/// <summary>
/// One hit slot, and the drift amplitudes derived from it (design-spec §6.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Amplitude is derived, not chosen.</b> It follows from pitch and icon extent, so it scales
/// with per-monitor DPI automatically and is never expressed as an absolute pixel count. This is
/// what answers "how far should it slide": as far as the slot allows and not one pixel further.
/// </para>
/// <para>
/// The honest consequence, stated in §6.0: slot-bounded drift provides <b>no</b> peak-wear
/// reduction for icon interiors. Meaningful interior reduction needs amplitude greater than the
/// icon's own width, which is nearly three times the whole gutter. Interiors are outline mode's
/// job (§5.3), not motion's.
/// </para>
/// </remarks>
public readonly record struct SlotMetrics
{
    /// <summary>Slot pitch at 100% scaling, in pixels.</summary>
    public const double BasePitch = 44.0;

    /// <summary>Icon extent at 100% scaling, in pixels.</summary>
    public const double BaseIconExtent = 24.0;

    /// <summary>Fraction of pitch held back from the gutter as a safety margin (§6.2).</summary>
    public const double SafetyMarginFraction = 0.05;

    /// <summary>Drift amplitude as a fraction of <see cref="MaxAmplitude"/> (§6.2).</summary>
    public const double DriftFraction = 0.70;

    /// <summary>Micro-jitter amplitude as a fraction of <see cref="MaxAmplitude"/> (§6.1).</summary>
    public const double MicroFraction = 0.25;

    /// <summary>Creates slot metrics from a pitch and the extent of the content inside it.</summary>
    public SlotMetrics(double pitch, double iconExtent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pitch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iconExtent);
        if (iconExtent > pitch)
            throw new ArgumentOutOfRangeException(nameof(iconExtent), "Content cannot be wider than its slot.");

        Pitch = pitch;
        IconExtent = iconExtent;
    }

    /// <summary>Slot pitch — the hit target's extent along the bar's long axis.</summary>
    public double Pitch { get; }

    /// <summary>Extent of the drawn content inside the slot.</summary>
    public double IconExtent { get; }

    /// <summary>Metrics at a DPI scale factor: 1.0 is 100%, 1.5 is 150%.</summary>
    public static SlotMetrics ForScale(double scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        return new SlotMetrics(BasePitch * scale, BaseIconExtent * scale);
    }

    /// <summary>Free space either side of the content, <c>(p − e) / 2</c>. The hard bound under P5.</summary>
    public double Gutter => (Pitch - IconExtent) / 2.0;

    /// <summary>The 5%-of-pitch safety margin held back from the gutter.</summary>
    public double SafetyMargin => SafetyMarginFraction * Pitch;

    /// <summary>Usable amplitude, <c>A_max = (p − e) / 2 − 0.05·p</c>.</summary>
    public double MaxAmplitude => Gutter - SafetyMargin;

    /// <summary>Slot-bounded drift amplitude, <c>0.70 × A_max</c> (§6.2).</summary>
    public double DriftAmplitude => DriftFraction * MaxAmplitude;

    /// <summary>Micro-jitter amplitude, <c>0.25 × A_max</c> (§6.1).</summary>
    public double MicroAmplitude => MicroFraction * MaxAmplitude;

    /// <summary>
    /// Worst-case drawn offset. Drift and micro-jitter are additive and together must never
    /// exceed the gutter.
    /// </summary>
    public double PeakOffset => DriftAmplitude + MicroAmplitude;

    /// <summary>Space left between the content at peak offset and the edge of its own slot.</summary>
    public double Clearance => Gutter - PeakOffset;

    /// <summary>
    /// P5, as a predicate. Drawn content never leaves its own hit slot, at any drift phase.
    /// </summary>
    public bool SatisfiesSlotContainment => PeakOffset < Gutter;
}
