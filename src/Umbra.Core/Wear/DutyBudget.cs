using Umbra.Core.Color;

namespace Umbra.Core.Wear;

/// <summary>
/// Fraction of wall-clock time an element spends in each tier (design-spec §2.2, §3).
/// </summary>
/// <remarks>
/// Tier 0 and Tier 1 together are ~100% duty and together constitute the entire wear budget (P3).
/// Tier 3 is rare and excluded; it is accounted separately in <see cref="Tiers.NotificationBadge"/>.
/// </remarks>
public readonly record struct TierDuty(double Tier0, double Tier1, double Tier2)
{
    /// <summary>
    /// The shipping figures (§2.3): a 120 px proximity band puts Tier 0 at 92% and Tier 1 at 8%,
    /// with Tier 2 assumed at 0.1% — 1.4 min/day.
    /// </summary>
    public static TierDuty Current => new(0.92, 0.08, 0.001);

    /// <summary>
    /// v1's figures (§2.2), kept as a regression baseline. A 200 px band inflated Tier 1 to 15%,
    /// and Tier 1 turned out to be a third of the entire budget.
    /// </summary>
    public static TierDuty V1 => new(0.85, 0.15, 0.001);

    /// <summary>
    /// The same duty with Tier 2 at <paramref name="tier2"/>. Tier 2 triggers on proximity rather
    /// than on click, so 1% — ten times the assumption — is entirely plausible, and §2.3 requires
    /// the design to survive it.
    /// </summary>
    public TierDuty WithTier2(double tier2) => this with { Tier2 = tier2 };
}

/// <summary>Duty-weighted wear per second, broken down by tier.</summary>
public readonly record struct WearBudget(double Tier0, double Tier1, double Tier2)
{
    /// <summary>Total wear per second.</summary>
    public double Total => Tier0 + Tier1 + Tier2;

    /// <summary>Tier 0's share of the total, 0..1.</summary>
    public double Tier0Share => Share(Tier0);

    /// <summary>Tier 1's share of the total, 0..1.</summary>
    public double Tier1Share => Share(Tier1);

    /// <summary>Tier 2's share of the total, 0..1.</summary>
    public double Tier2Share => Share(Tier2);

    /// <summary>How many times cheaper this budget is than <paramref name="other"/>.</summary>
    public double ImprovementOver(WearBudget other) => other.Total / Total;

    private double Share(double component)
    {
        double total = Total;
        return total <= 0.0 ? 0.0 : component / total;
    }
}

/// <summary>
/// The duty-weighted wear budget of design-spec §2.2–2.3 — the calculation that replaced v1's
/// single-state comparison.
/// </summary>
/// <remarks>
/// Comparing rest states as if each ran at 100% duty is what produced v1's claim that a full-white
/// hover "costs an order of magnitude less than even a very dark rest state". It was 3×. Weight
/// each tier by its duty and sum, or the conclusions come out wrong.
/// </remarks>
public static class DutyBudget
{
    /// <summary>
    /// Reference luminance used for a luminance-capped Tier 2 in the §2.3 budget tables.
    /// </summary>
    /// <remarks>
    /// This is <c>L(#B3B3B3) = 0.450786</c>, the value the published budget figures were computed
    /// with, and it is what reproduces 0.003574 and 0.007115. Note it sits 0.17% above
    /// <see cref="Tiers.TierPalette.Tier2LuminanceCap"/>, the 0.45 the renderer actually enforces;
    /// substituting 0.45 here moves the pinned total to 0.003573. Immaterial to every conclusion
    /// in §2, but the two constants are not the same number and should not be conflated.
    /// </remarks>
    public static double Tier2CappedLuminance { get; } = Srgb.RelativeLuminance(Rgb.FromHex("#B3B3B3"));

    /// <summary>Tier 2 at full white — what v1 spent, before the §3.2 cap.</summary>
    public const double Tier2UncappedLuminance = 1.0;

    /// <summary>Duty-weights three tier luminances into a wear budget.</summary>
    public static WearBudget Evaluate(
        TierDuty duty,
        double tier0Luminance,
        double tier1Luminance,
        double tier2Luminance,
        double exponent = WearModel.DefaultExponent) =>
        new(
            duty.Tier0 * WearModel.Rate(tier0Luminance, exponent),
            duty.Tier1 * WearModel.Rate(tier1Luminance, exponent),
            duty.Tier2 * WearModel.Rate(tier2Luminance, exponent));

    /// <summary>Duty-weights an element's rest colours and its Tier 2 luminance into a wear budget.</summary>
    public static WearBudget Evaluate(
        TierDuty duty,
        Rgb tier0,
        Rgb tier1,
        double tier2Luminance,
        double exponent = WearModel.DefaultExponent) =>
        Evaluate(
            duty,
            Srgb.RelativeLuminance(tier0),
            Srgb.RelativeLuminance(tier1),
            tier2Luminance,
            exponent);
}
