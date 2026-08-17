namespace Umbra.Core.Motion;

/// <summary>Reasons motion stops, per design-spec §6.6.</summary>
[Flags]
public enum MotionSuppression
{
    /// <summary>Motion runs.</summary>
    None = 0,

    /// <summary>Session lock.</summary>
    SessionLocked = 1 << 0,

    /// <summary>User idle beyond <see cref="MotionClock.IdleThresholdSeconds"/>.</summary>
    UserIdle = 1 << 1,

    /// <summary>Display powered down (DPMS off).</summary>
    DisplayOff = 1 << 2,

    /// <summary>Exclusive fullscreen — the bar is hidden anyway, and the render loop should stop too.</summary>
    ExclusiveFullscreen = 1 << 3,

    /// <summary>The system "show animations in Windows" setting is off.</summary>
    AnimationsDisabled = 1 << 4,

    /// <summary>An OS reduced-motion signal.</summary>
    ReducedMotion = 1 << 5,
}

/// <summary>
/// The motion layer's clock. Accumulates motion time while running and holds its phase while
/// suppressed (design-spec §6.6).
/// </summary>
/// <remarks>
/// <para>
/// Pausing on idle and display-off is not merely a power optimisation. WOLED panels run their own
/// compensation cycles when idle or on standby, and a process that repaints continuously can keep
/// the display awake and prevent those cycles from running — a motion layer that suppresses the
/// panel's own far more effective mitigation would be a net loss.
/// </para>
/// <para>
/// <b>This is necessary but may not be sufficient.</b> ManagedShell runs two permanent 100 ms
/// polling timers that we do not control and cannot pause without losing the tray and letting
/// Explorer's taskbar reappear. If those alone defeat display idle, §6.6's argument collapses.
/// Spec §11.2 Q2 — measure it before relying on any of this.
/// </para>
/// <para>
/// Motion resumes from the stored phase, never from zero: restarting at zero would snap the
/// content group back to the extreme of its travel, which is both perceptible and a wear event.
/// </para>
/// </remarks>
public sealed class MotionClock
{
    /// <summary>User idle threshold, 5 minutes (§6.6).</summary>
    public const double IdleThresholdSeconds = 300.0;

    /// <summary>Creates a clock at the given starting phase, in seconds of motion time.</summary>
    public MotionClock(double phaseSeconds = 0.0) => PhaseSeconds = phaseSeconds;

    /// <summary>
    /// Accumulated motion time. This is what feeds <see cref="MotionModel"/>, and what must be
    /// persisted and restored across a pause — not wall-clock time.
    /// </summary>
    public double PhaseSeconds { get; private set; }

    /// <summary>Why motion is currently stopped, if it is.</summary>
    public MotionSuppression Suppression { get; private set; }

    /// <summary>True when no suppression reason applies.</summary>
    public bool IsRunning => Suppression == MotionSuppression.None;

    /// <summary>Advances motion time by <paramref name="seconds"/>. A no-op while suppressed.</summary>
    public void Advance(double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        if (IsRunning) PhaseSeconds += seconds;
    }

    /// <summary>Adds a suppression reason.</summary>
    public void Suppress(MotionSuppression reason) => Suppression |= reason;

    /// <summary>Clears a suppression reason. Motion resumes only once every reason is clear.</summary>
    public void Release(MotionSuppression reason) => Suppression &= ~reason;

    /// <summary>Replaces the whole suppression set — for driving the clock from observed system state.</summary>
    public void SetSuppression(MotionSuppression reasons) => Suppression = reasons;
}
