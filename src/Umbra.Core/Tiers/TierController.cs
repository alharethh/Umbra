namespace Umbra.Core.Tiers;

/// <summary>
/// Drives the continuous tier level from pointer proximity, with the attack, release and dwell
/// behaviour of design-spec §3.4.
/// </summary>
/// <remarks>
/// <para>
/// The proximity band is <b>120 px at 100% scaling and scales with per-monitor DPI</b>. v1
/// specified 200 px in raw pixels, which both inflated Tier 1 duty and meant the band covered a
/// different fraction of the screen on every monitor.
/// </para>
/// <para>
/// The level is continuous in 0..2 because Tier 0 → Tier 1 ramps rather than snaps. Tier 2 is a
/// step, gated by the dwell guard: it does not promote until the pointer has been inside the
/// engaged band for 60 ms, which stops mouse travel that merely crosses the bar from lighting it
/// fully.
/// </para>
/// </remarks>
public sealed class TierController
{
    /// <summary>Attack time, 120 ms — one tier step, dimmer to brighter (§3.4).</summary>
    public const double AttackSeconds = 0.120;

    /// <summary>
    /// Release time, 400 ms — one tier step, brighter to dimmer. Deliberately slower than the
    /// attack so the bar does not flicker during transient pointer passes.
    /// </summary>
    public const double ReleaseSeconds = 0.400;

    /// <summary>Dwell required inside the engaged band before Tier 2 promotes, 60 ms.</summary>
    public const double DwellSeconds = 0.060;

    private double _dwell;

    /// <summary>Creates a controller for a monitor at the given DPI scale.</summary>
    public TierController(double scale = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        Scale = scale;
    }

    /// <summary>Per-monitor DPI scale: 1.0 is 100%.</summary>
    public double Scale { get; }

    /// <summary>Ambient band radius in scaled pixels.</summary>
    public double AmbientBand => TierPalette.AmbientBandPixels * Scale;

    /// <summary>Engaged band radius in scaled pixels.</summary>
    public double EngagedBand => TierPalette.EngagedBandPixels * Scale;

    /// <summary>Current continuous level, 0..2.</summary>
    public double Level { get; private set; }

    /// <summary>The nearest whole tier to <see cref="Level"/>.</summary>
    public Tier Tier => (Tier)(int)Math.Round(Level, MidpointRounding.AwayFromZero);

    /// <summary>Seconds the pointer has been continuously inside the engaged band.</summary>
    public double DwellSecondsElapsed => _dwell;

    /// <summary>
    /// The level the controller is heading for, before attack and release rate-limiting.
    /// </summary>
    /// <param name="distance">Pointer distance to the bar, in pixels. Negative counts as zero.</param>
    /// <param name="dwellSatisfied">
    /// Whether the pointer has been inside the engaged band long enough to promote to Tier 2.
    /// Until it has, the target is capped at Tier 1 — the top of the proximity ramp.
    /// </param>
    /// <param name="keyboardFocus">
    /// Keyboard focus promotes to Tier 2 with no pointer proximity at all (§3), since
    /// <c>Win+T</c> and arrow navigation have no pointer path.
    /// </param>
    public double TargetLevel(double distance, bool dwellSatisfied = true, bool keyboardFocus = false)
    {
        if (keyboardFocus) return 2.0;

        distance = Math.Max(0.0, distance);

        if (distance <= EngagedBand) return dwellSatisfied ? 2.0 : 1.0;
        if (distance >= AmbientBand) return 0.0;

        // Linear ramp across the band: 1.0 at the engaged edge, 0.0 at the ambient edge.
        return (AmbientBand - distance) / (AmbientBand - EngagedBand);
    }

    /// <summary>
    /// Advances the controller by <paramref name="seconds"/> with the pointer at
    /// <paramref name="distance"/> pixels from the bar.
    /// </summary>
    public void Update(double distance, double seconds, bool keyboardFocus = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);

        bool inEngagedBand = Math.Max(0.0, distance) <= EngagedBand;
        _dwell = inEngagedBand ? _dwell + seconds : 0.0;

        double target = TargetLevel(distance, _dwell >= DwellSeconds, keyboardFocus);
        double rate = target > Level ? 1.0 / AttackSeconds : 1.0 / ReleaseSeconds;
        double step = rate * seconds;

        Level = Math.Clamp(
            Level + Math.Clamp(target - Level, -step, step),
            0.0,
            2.0);
    }

    /// <summary>Snaps straight to a level — for start-up, and for tests.</summary>
    public void Reset(double level = 0.0)
    {
        Level = Math.Clamp(level, 0.0, 2.0);
        _dwell = 0.0;
    }
}

/// <summary>
/// The §3.4 transition shapes, for the render layer to apply on top of
/// <see cref="TierController.Level"/>.
/// </summary>
/// <remarks>
/// Attack eases out, release eases in. Note that easing is right here and wrong for drift: a tier
/// transition is a sub-second event whose shape is about perception, whereas drift easing would
/// concentrate residence time at the extremes and burn two hot bands (§6.2).
/// </remarks>
public static class TierEasing
{
    /// <summary>Ease-out (quadratic), for dimmer → brighter.</summary>
    public static double Attack(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return 1.0 - (1.0 - t) * (1.0 - t);
    }

    /// <summary>Ease-in (quadratic), for brighter → dimmer.</summary>
    public static double Release(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t;
    }
}
