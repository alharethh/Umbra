using Umbra.Core.Tiers;
using Umbra.Core.Wear;

namespace Umbra.Core.Tests;

/// <summary>The luminance model of design-spec §2.1 and the four figures pinned in verified-numbers.md.</summary>
public class LuminanceTests
{
    // The §2.1 table: hex, relative luminance, relative wear at n = 1.7, and the "vs always-on
    // white" column. L is published to 2 dp as a percentage, wear to 6 dp, the ratio to 5 sf.
    public static TheoryData<string, double, double, double> LuminanceTable => new()
    {
        { "#000000", 0.0000, 0.000000, double.PositiveInfinity },
        { "#0A0A0A", 0.0030, 0.000052, 19066 },
        { "#1A1A1A", 0.0103, 0.000421, 2377 },
        { "#333333", 0.0331, 0.003047, 328 },
        { "#404040", 0.0513, 0.006409, 156 },
        { "#4D4D4D", 0.0742, 0.012018, 83 },
        { "#666666", 0.1329, 0.032346, 31 },
        { "#808080", 0.2159, 0.073806, 13.5 },
        { "#B3B3B3", 0.4508, 0.258077, 3.9 },
        { "#FFFFFF", 1.0000, 1.000000, 1 },
    };

    [Theory]
    [MemberData(nameof(LuminanceTable))]
    public void Section21_Table_Reproduces(string hex, double luminance, double wear, double ratioToWhite)
    {
        Rgb colour = Rgb.FromHex(hex);

        Approx.Equal(luminance, Srgb.RelativeLuminance(colour), 5e-5, $"L({hex})");
        Approx.Equal(wear, WearModel.Rate(colour), 1e-6, $"wear({hex})");

        double computedRatio = WearModel.LifetimeRatio(Srgb.RelativeLuminance(colour));
        if (double.IsPositiveInfinity(ratioToWhite))
            Assert.True(double.IsPositiveInfinity(computedRatio), "Black must be free — P1.");
        else
            Approx.Relative(ratioToWhite, computedRatio, 0.01, $"{hex} vs always-on white");
    }

    // verified-numbers.md publishes these four to 6 significant figures.
    [Theory]
    [InlineData("#333333", 0.0331048)]
    [InlineData("#404040", 0.0512695)]
    [InlineData("#4D4D4D", 0.0742136)]
    [InlineData("#B3B3B3", 0.450786)]
    public void VerifiedLuminances(string hex, double documented) =>
        Approx.Equal(documented, Srgb.RelativeLuminance(Rgb.FromHex(hex)), 1e-6, $"L({hex})");

    [Fact]
    public void VerifiedWear_Of_Tier0PinnedIcon() =>
        Approx.Equal(0.00304659, WearModel.Rate(Rgb.FromHex("#333333")), 1e-8, "wear(#333333), n = 1.7");

    [Fact]
    public void VerifiedWear_Of_Tier2Cap() =>
        Approx.Equal(0.258077, WearModel.Rate(DutyBudget.Tier2CappedLuminance), 1e-6,
            "Tier 2 cap wear at L = 0.4508");

    [Fact]
    public void Gamma_Is_Not_Linear()
    {
        // §2.1: "#808080 is not half-bright, it is ~22% of peak. Reason in luminance, not in hex."
        double mid = Srgb.RelativeLuminance(Rgb.FromHex("#808080"));

        Approx.Equal(0.2159, mid, 5e-5, "L(#808080)");
        Assert.True(mid < 0.25, "Half-way in hex is nowhere near half-way in light.");
    }

    [Fact]
    public void TransferFunctions_RoundTrip()
    {
        for (int channel = 0; channel <= 255; channel++)
            Assert.Equal(channel, Srgb.ToByte(Srgb.ToLinear((byte)channel)));
    }

    [Fact]
    public void LinearSegment_Meets_PowerSegment_At_The_Knee()
    {
        // sRGB's two segments do not join perfectly — the standard's own constants leave a step of
        // ~1e-7 in linear light. That is far below anything the ramp can show; what matters is
        // that it stays that small rather than becoming a visible notch in the Tier 0 → Tier 1
        // interpolation.
        const double knee = Srgb.LinearSegmentEnd;

        Approx.Equal(Srgb.ToLinear(knee), Srgb.ToLinear(knee + 1e-12), 1e-6, "sRGB decode at the knee");
    }

    [Fact]
    public void Scale_Works_In_Linear_Light()
    {
        Rgb source = Rgb.FromHex("#B3B3B3");
        double luminance = Srgb.RelativeLuminance(source);

        Rgb halved = Srgb.Scale(source, 0.5);

        // The result has to land in an 8-bit channel, and at this level one step is ~0.7% of
        // luminance — so assert against the quantisation grid rather than against the ideal.
        double ideal = Srgb.ToEncoded(luminance / 2.0) * 255.0;
        Approx.Equal(ideal, halved.R, 0.5, "halving linear light, rounded to the nearest 8-bit step");
        Approx.Relative(luminance / 2.0, Srgb.RelativeLuminance(halved), 0.01,
            "halving linear light halves luminance");

        Assert.NotEqual(source.R / 2, halved.R);   // and is not halving the encoded value
    }

    [Fact]
    public void CapLuminance_Preserves_Chromaticity()
    {
        Rgb vivid = Rgb.FromHex("#2F7DE1");
        Rgb capped = Srgb.CapLuminance(vivid, 0.45);

        Assert.True(Srgb.RelativeLuminance(vivid) < 0.45, "Precondition: this sample is already below the cap.");
        Assert.Equal(vivid, capped);
    }

    [Fact]
    public void CapLuminance_Tone_Maps_To_The_Cap()
    {
        // §3.2: icons are tone-mapped so peak luminance lands on the cap; hue and saturation are
        // untouched. The match is only as good as 8-bit quantisation allows.
        Rgb bright = Rgb.FromHex("#F2F2F2");
        Rgb capped = Srgb.CapLuminance(bright, TierPalette.Tier2LuminanceCap);

        Approx.Equal(TierPalette.Tier2LuminanceCap, Srgb.RelativeLuminance(capped), 0.005, "capped luminance");

        (double r, double g, double b) = Srgb.ToLinear(capped);
        Approx.Relative(1.0, r / g, 0.02, "neutral stays neutral");
        Approx.Relative(1.0, g / b, 0.02, "neutral stays neutral");
    }

    [Fact]
    public void Lerp_Halfway_Is_Halfway_In_Light_Not_In_Hex()
    {
        // The §3.4 rule at full contrast, where the two methods are unmistakably different.
        Rgb target = Rgb.FromHex("#B3B3B3");
        double full = Srgb.RelativeLuminance(target);

        Rgb midpoint = Srgb.Lerp(Rgb.Black, target, 0.5);
        Rgb encodedMidpoint = new((byte)(target.R / 2), (byte)(target.G / 2), (byte)(target.B / 2));

        Approx.Relative(full / 2.0, Srgb.RelativeLuminance(midpoint), 0.01, "linear-light midpoint");
        Approx.Relative(0.0999, Srgb.RelativeLuminance(encodedMidpoint), 0.01, "encoded midpoint");

        // Interpolating hex would put the ramp's halfway point at 22% of the endpoint, not 50%.
        Assert.True(
            Srgb.RelativeLuminance(midpoint) > Srgb.RelativeLuminance(encodedMidpoint) * 2.0,
            "Reason in luminance, not in hex.");
    }

    [Fact]
    public void Hex_RoundTrips_Independently_Of_Culture()
    {
        Assert.Equal("#4D4D4D", Rgb.FromHex("#4d4d4d").ToHex());
        Assert.Equal(Rgb.FromHex("#4D4D4D"), Rgb.FromHex("4D4D4D"));
    }
}
