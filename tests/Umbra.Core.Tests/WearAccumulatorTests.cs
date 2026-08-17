using Umbra.Core.Wear;

namespace Umbra.Core.Tests;

/// <summary>Design-spec §9 — wear accounting, and the two v1 corrections it records.</summary>
public class WearAccumulatorTests
{
    /// <summary>A typical pinned-icon rate: the v3 duty-weighted total, 0.003574 wear/s.</summary>
    private const double PinnedIconRate = 0.003574;

    /// <summary>Five million per-second additions — just under two months of runtime.</summary>
    private const int TwoMonthsOfSamples = 5_000_000;

    [Fact]
    public void Accumulates_The_Superlinear_Exponent_Not_Emitted_Light()
    {
        // The first v1 correction. Accumulating L·Δt measures emitted light rather than
        // degradation, contradicts §2's central claim, and systematically under-ranks bright
        // elements — precisely backwards for a tool whose job is to find them.
        WearAccumulator accumulator = new(2, 1);

        accumulator.Add(0, 0, luminance: 0.5, seconds: 100.0);

        double superlinear = Math.Pow(0.5, WearModel.DefaultExponent) * 100.0;
        double linear = 0.5 * 100.0;

        Approx.Relative(superlinear, accumulator[0, 0], 1e-12, "accumulated wear");
        Assert.True(accumulator[0, 0] < linear, "L^1.7 must sit below L for L < 1 — that is the whole point.");
    }

    [Fact]
    public void Exponent_Travels_With_The_Data()
    {
        // §9: store n alongside the data so the ranking can be recomputed for a different exponent
        // without re-collecting.
        WearAccumulator conservative = new(1, 1, exponent: 1.5);
        WearAccumulator aggressive = new(1, 1, exponent: 2.0);

        conservative.Add(0, 0, 0.5, 1.0);
        aggressive.Add(0, 0, 0.5, 1.0);

        Assert.Equal(1.5, conservative.Exponent);
        Assert.Equal(2.0, aggressive.Exponent);
        Assert.True(aggressive[0, 0] < conservative[0, 0], "A higher exponent punishes a dim pixel harder.");
    }

    [Fact]
    public void Reserved_Rect_Buffer_Is_1680_KB_At_3840x56()
    {
        WearAccumulator accumulator = new(3840, 56);

        Assert.Equal(1_720_320L, accumulator.SizeInBytes);
        Approx.Equal(1680.0, accumulator.SizeInBytes / 1024.0, 0.5, "float64 buffer over the reserved rect");
    }

    [Fact]
    public void Float32_Drifts_About_Four_Percent_Within_Two_Months()
    {
        // The second v1 correction, and the reason §9 specifies float64. The failure is silent:
        // the heatmap still looks entirely plausible.
        double exact = PinnedIconRate * TwoMonthsOfSamples;

        float naive = 0f;
        for (int i = 0; i < TwoMonthsOfSamples; i++) naive += (float)PinnedIconRate;

        double error = Math.Abs(naive - exact) / exact;

        Assert.InRange(error, 0.03, 0.05);          // documented as ~4.2%; measured 4.15%
        Assert.True(error > 0.01, "float32 is not merely imprecise here, it is wrong by percent.");
    }

    [Fact]
    public void Float64_Tracks_The_Exact_Total()
    {
        double exact = PinnedIconRate * TwoMonthsOfSamples;

        double accumulated = 0.0;
        for (int i = 0; i < TwoMonthsOfSamples; i++) accumulated += PinnedIconRate;

        Approx.Relative(exact, accumulated, 1e-9, "float64 accumulation over two months");
    }

    [Fact]
    public void Hourly_Folding_Rescues_A_Float32_Buffer()
    {
        // §9's fallback if memory ever matters: accumulate per-second into a small float32 buffer
        // and fold it into a float64 accumulator hourly. Note the spec says this "tracks the exact
        // value to 5 decimal places" — it is five *significant figures*, about 2e-5 relative.
        FoldingAccumulator folding = new();

        // Feed the rate directly, so the comparison isolates the accumulation strategy.
        double luminance = Math.Pow(PinnedIconRate, 1.0 / WearModel.DefaultExponent);
        for (int i = 0; i < TwoMonthsOfSamples; i++) folding.Add(luminance, 1.0);

        double exact = WearModel.Rate(luminance) * TwoMonthsOfSamples;

        Approx.Relative(exact, folding.Total, 1e-4, "hourly-folded accumulation");
        Assert.Equal(TwoMonthsOfSamples / 3600, folding.FoldCount);
    }

    [Fact]
    public void AddFrame_Advances_Wall_Clock_And_Every_Cell()
    {
        WearAccumulator accumulator = new(2, 2);
        double[] frame = [0.0, 0.5, 0.5, 1.0];

        accumulator.AddFrame(frame, 1.0);
        accumulator.AddFrame(frame, 1.0);

        Assert.Equal(2.0, accumulator.ElapsedSeconds);
        Assert.Equal(0.0, accumulator[0, 0]);                            // P1: black is free
        Approx.Relative(2.0 * WearModel.Rate(0.5), accumulator[1, 0], 1e-12, "mid-grey cell");
        Approx.Relative(2.0, accumulator[1, 1], 1e-12, "white cell");
        Approx.Relative(2.0, accumulator.Peak, 1e-12, "peak cell");
    }

    [Fact]
    public void Idle_Keeps_The_Denominator_Honest()
    {
        // Motion pauses on lock, idle and DPMS off (§6.6). Wall-clock time still passes, and a
        // dataset that forgot those hours would overstate the bar's average brightness.
        WearAccumulator accumulator = new(1, 1);

        accumulator.AddFrame([1.0], 10.0);
        accumulator.Idle(90.0);

        Assert.Equal(100.0, accumulator.ElapsedSeconds);
        Approx.Relative(10.0, accumulator.Total, 1e-12, "wear accumulated while lit");
    }

    [Fact]
    public void Normalised_Is_A_Heatmap()
    {
        WearAccumulator accumulator = new(3, 1);
        accumulator.AddFrame([0.0, 0.5, 1.0], 1.0);

        double[] heatmap = accumulator.Normalised();

        Assert.Equal(0.0, heatmap[0]);
        Assert.Equal(1.0, heatmap[2]);
        Assert.InRange(heatmap[1], 0.0, 1.0);
    }

    [Fact]
    public void Empty_Accumulator_Normalises_Without_Dividing_By_Zero()
    {
        WearAccumulator accumulator = new(2, 2);

        Assert.All(accumulator.Normalised(), value => Assert.Equal(0.0, value));
    }
}
