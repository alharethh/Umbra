using Umbra.Core.Wear;

namespace Umbra.Core.Tests;

/// <summary>
/// The duty-weighted wear budget of design-spec §2.2–2.4.
/// </summary>
/// <remarks>
/// Several published totals are truncated rather than rounded (0.007115 for a computed 0.0071154,
/// 0.005392 for 0.0053923), so the tolerance here is one unit in the last published place.
/// </remarks>
public class DutyBudgetTests
{
    private static readonly Rgb Tier0Pinned = Rgb.FromHex("#333333");
    private static readonly Rgb Tier1Pinned = Rgb.FromHex("#404040");
    private static readonly Rgb Tier0Running = Rgb.FromHex("#404040");
    private static readonly Rgb Tier1Running = Rgb.FromHex("#4D4D4D");
    private static readonly Rgb V1Tier1Running = Rgb.FromHex("#666666");

    private static readonly double Cap = DutyBudget.Tier2CappedLuminance;

    private static WearBudget V3Pinned => DutyBudget.Evaluate(TierDuty.Current, Tier0Pinned, Tier1Pinned, Cap);
    private static WearBudget V3Running => DutyBudget.Evaluate(TierDuty.Current, Tier0Running, Tier1Running, Cap);

    private static WearBudget V1Pinned =>
        DutyBudget.Evaluate(TierDuty.V1, Tier0Pinned, Tier1Running, DutyBudget.Tier2UncappedLuminance);

    private static WearBudget V1Running =>
        DutyBudget.Evaluate(TierDuty.V1, Tier0Running, V1Tier1Running, DutyBudget.Tier2UncappedLuminance);

    [Fact]
    public void V3_PinnedIcon_Is_The_Published_Total()
    {
        Approx.Equal(0.003574, V3Pinned.Total, 1e-6, "v3 pinned icon, duty-weighted");

        Approx.Equal(0.784, V3Pinned.Tier0Share, 0.001, "Tier 0 share");
        Approx.Equal(0.143, V3Pinned.Tier1Share, 0.001, "Tier 1 share");
        Approx.Equal(0.072, V3Pinned.Tier2Share, 0.001, "Tier 2 share");
    }

    [Fact]
    public void V3_RunningIcon_Is_The_Published_Total()
    {
        Approx.Equal(0.007115, V3Running.Total, 1e-6, "v3 running icon, duty-weighted");

        Approx.Equal(0.829, V3Running.Tier0Share, 0.001, "Tier 0 share");
        Approx.Equal(0.135, V3Running.Tier1Share, 0.001, "Tier 1 share");
        Approx.Equal(0.036, V3Running.Tier2Share, 0.001, "Tier 2 share");
    }

    [Fact]
    public void V1_Regression_Baselines()
    {
        Approx.Equal(0.005392, V1Pinned.Total, 1e-6, "v1 pinned (regression)");
        Approx.Equal(0.011299, V1Running.Total, 1e-6, "v1 running (regression)");
    }

    [Fact]
    public void V1_Tier1_Was_A_Third_To_A_Half_Of_The_Entire_Budget()
    {
        // §2.2, the finding that redirected the whole design: v1's instruction to "optimise rest
        // ruthlessly; spend freely on hover" was aimed at the wrong state. Tier 1 *is* rest, it
        // just has a pointer nearby.
        Approx.Equal(0.334, V1Pinned.Tier1Share, 0.001, "v1 pinned Tier 1 share");
        Approx.Equal(0.429, V1Running.Tier1Share, 0.001, "v1 running Tier 1 share");

        Assert.True(V1Pinned.Tier1Share > 0.33, "Tier 1 was a third of the budget, not a transient.");
    }

    [Fact]
    public void The_Order_Of_Magnitude_Claim_Was_Three_Times_Not_Ten()
    {
        // v1 called this "the single most important number in the document". It was wrong, so §2.2
        // corrects it rather than quietly dropping it: #333333 at 100% duty is 328× below
        // always-on white, and white at 0.1% duty is 1000× below. That is 3×, not an order of
        // magnitude.
        double darkRestState = WearModel.Rate(Tier0Pinned);
        double whiteAtHoverDuty = 0.001 * WearModel.Rate(1.0);

        Approx.Relative(328.0, 1.0 / darkRestState, 0.01, "#333333 vs always-on white");
        Approx.Relative(1000.0, 1.0 / whiteAtHoverDuty, 0.01, "0.1%-duty white vs always-on white");

        double gap = darkRestState / whiteAtHoverDuty;
        Approx.Relative(3.0, gap, 0.05, "the actual gap");
        Assert.True(gap < 10.0, "It was never an order of magnitude.");
    }

    [Fact]
    public void The_Tier2_Cap_Makes_That_Claim_True_Again()
    {
        // §2.3: a capped-luminance hover at 0.1% duty is 3876× below always-on white against
        // #333333's 328×, which genuinely is an order of magnitude cheaper than the rest state.
        double cappedHover = 0.001 * WearModel.Rate(Cap);

        Approx.Relative(3876.0, 1.0 / cappedHover, 0.01, "capped hover vs always-on white");

        double gap = WearModel.Rate(Tier0Pinned) / cappedHover;
        Assert.True(gap > 10.0, $"The sentence v1 wanted to write becomes true once Tier 2 is capped (got {gap:F1}×).");
    }

    [Fact]
    public void V3_Is_Better_Than_V1_By_The_Published_Factors()
    {
        Approx.Equal(1.51, V1Pinned.Total / V3Pinned.Total, 0.01, "pinned improvement over v1");
        Approx.Equal(1.59, V1Running.Total / V3Running.Total, 0.01, "running improvement over v1");
    }

    [Fact]
    public void V1_Inverts_Entirely_If_Tier2_Duty_Was_Underestimated()
    {
        // §2.2's second problem: Tier 2 triggers on proximity, not on click, so 1% duty is
        // entirely plausible — and at 1% the whole v1 thesis inverts on a parameter nobody has
        // measured. That is not an acceptable place for a design to sit.
        TierDuty pessimistic = TierDuty.V1.WithTier2(0.01);

        WearBudget pinned = DutyBudget.Evaluate(pessimistic, Tier0Pinned, Tier1Running, 1.0);
        WearBudget running = DutyBudget.Evaluate(pessimistic, Tier0Running, V1Tier1Running, 1.0);

        Approx.Equal(0.014392, pinned.Total, 1e-6, "v1 pinned at 1% Tier 2 duty");
        Approx.Equal(0.020299, running.Total, 1e-6, "v1 running at 1% Tier 2 duty");

        Approx.Equal(0.695, pinned.Tier2Share, 0.001, "v1 pinned Tier 2 share");
        Approx.Equal(0.493, running.Tier2Share, 0.001, "v1 running Tier 2 share");

        Assert.True(pinned.Tier2Share > 0.5, "Under v1 a single unmeasured parameter dominates the budget.");
    }

    [Fact]
    public void V3_Survives_The_Same_Pessimism()
    {
        TierDuty pessimistic = TierDuty.Current.WithTier2(0.01);

        WearBudget pinned = DutyBudget.Evaluate(pessimistic, Tier0Pinned, Tier1Pinned, Cap);
        WearBudget running = DutyBudget.Evaluate(pessimistic, Tier0Running, Tier1Running, Cap);

        Approx.Equal(0.005896, pinned.Total, 1e-6, "v3 pinned at 1% Tier 2 duty");
        Approx.Equal(0.009438, running.Total, 1e-6, "v3 running at 1% Tier 2 duty");

        Approx.Equal(0.438, pinned.Tier2Share, 0.001, "v3 pinned Tier 2 share");
        Approx.Equal(0.273, running.Tier2Share, 0.001, "v3 running Tier 2 share");

        Assert.True(pinned.Tier2Share < 0.5, "Tier 2 must not dominate even at ten times the assumed duty.");

        // The point of §2.3: v3's worst case is v1's best case, so the design no longer swings on
        // a number nobody has measured.
        Assert.True(pinned.Total <= V1Pinned.Total * 1.15,
            $"v3 pessimistic ({pinned.Total:F6}) should land near v1 optimistic ({V1Pinned.Total:F6}).");
    }

    [Fact]
    public void Duty_Weighting_Is_Not_The_Same_As_Comparing_Rest_States()
    {
        // The methodological point of §2.2. Comparing states at 100% duty in isolation is what
        // produced v1's wrong conclusion; the answer changes once each tier is weighted by how
        // long it actually runs.
        double unweighted = WearModel.Rate(Tier0Pinned);

        Assert.True(V3Pinned.Total > unweighted,
            "A duty-weighted budget includes Tier 1 and Tier 2, so it must exceed Tier 0 alone.");
        Assert.True(V3Pinned.Tier0Share < 1.0);
    }

    [Fact]
    public void Tier2_Reference_Luminance_Sits_Just_Above_The_Enforced_Cap()
    {
        // The published budgets were computed with L(#B3B3B3) = 0.450786; the renderer enforces
        // §3.2's L ≤ 0.45. The two differ by 0.17%, which moves the pinned total by one unit in
        // its sixth decimal — immaterial, but they are not the same constant.
        Approx.Equal(0.450786, Cap, 1e-6, "budget Tier 2 reference luminance");
        Assert.True(Cap > Tiers.TierPalette.Tier2LuminanceCap);
        Approx.Relative(Tiers.TierPalette.Tier2LuminanceCap, Cap, 0.002, "reference against enforced cap");

        WearBudget atEnforcedCap =
            DutyBudget.Evaluate(TierDuty.Current, Tier0Pinned, Tier1Pinned, Tiers.TierPalette.Tier2LuminanceCap);
        Approx.Equal(0.003573, atEnforcedCap.Total, 1e-6, "pinned total computed at the enforced cap");
    }
}
