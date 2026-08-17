using Umbra.Core.Color;

namespace Umbra.Core.Tiers;

/// <summary>
/// The unread-notification badge (design-spec §3.3) — after the Tier 2 cap, the brightest static
/// element in the entire design.
/// </summary>
/// <remarks>
/// <c>#B3B3B3</c> at L = 45%, in a tiny fixed position, potentially persisting for hours on an
/// unacknowledged count: textbook differential wear. v1 granted it an exemption with no mitigation
/// at all. Three rules apply — pulse rather than hold, drift on an independent frequency within
/// the icon's slot, and decay when ignored.
/// </remarks>
public static class NotificationBadge
{
    /// <summary>Badge colour before decay.</summary>
    public static Rgb Colour { get; } = Rgb.FromHex("#B3B3B3");

    /// <summary>Badge colour after it has been ignored for <see cref="DecayAfterSeconds"/>.</summary>
    public static Rgb DecayedColour { get; } = Rgb.FromHex("#808080");

    /// <summary>Lit phase of the pulse, 1 s.</summary>
    public const double PulseOnSeconds = 1.0;

    /// <summary>Dark phase of the pulse, 3 s. Quarters duty <i>and</i> reads as more attention-grabbing.</summary>
    public const double PulseOffSeconds = 3.0;

    /// <summary>Dark phase after decay, 6 s.</summary>
    public const double DecayedPulseOffSeconds = 6.0;

    /// <summary>How long an unacknowledged badge runs before stepping down, 15 minutes.</summary>
    public const double DecayAfterSeconds = 15.0 * 60.0;

    /// <summary>
    /// Pulse transition time, 200 ms — fades so the badge does not read as a fault indicator.
    /// </summary>
    public const double FadeSeconds = 0.200;

    /// <summary>Duty cycle of the undecayed pulse: 0.25.</summary>
    public const double PulseDuty = PulseOnSeconds / (PulseOnSeconds + PulseOffSeconds);

    /// <summary>Duty cycle after decay: 1/7.</summary>
    public const double DecayedPulseDuty = PulseOnSeconds / (PulseOnSeconds + DecayedPulseOffSeconds);

    /// <summary>Whether the badge has decayed after <paramref name="unacknowledgedSeconds"/>.</summary>
    public static bool HasDecayed(double unacknowledgedSeconds) =>
        unacknowledgedSeconds >= DecayAfterSeconds;

    /// <summary>Badge colour after the given time unacknowledged.</summary>
    public static Rgb ColourAt(double unacknowledgedSeconds) =>
        HasDecayed(unacknowledgedSeconds) ? DecayedColour : Colour;

    /// <summary>Duty cycle after the given time unacknowledged.</summary>
    public static double DutyAt(double unacknowledgedSeconds) =>
        HasDecayed(unacknowledgedSeconds) ? DecayedPulseDuty : PulseDuty;

    /// <summary>
    /// Duty-weighted wear per second of the badge, ignoring the 200 ms fades.
    /// </summary>
    public static double WearRate(double unacknowledgedSeconds,
                                  double exponent = Wear.WearModel.DefaultExponent) =>
        DutyAt(unacknowledgedSeconds) *
        Wear.WearModel.Rate(ColourAt(unacknowledgedSeconds), exponent);
}
