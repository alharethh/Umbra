using Umbra.Core.Icons;
using Umbra.Core.Tiers;

namespace Umbra.Core.Tests;

/// <summary>Design-spec §5.1 — monochrome normalisation, and the claim v2 withdrew.</summary>
public class IconNormalizerTests
{
    [Fact]
    public void P90_Ignores_A_Single_Stray_Bright_Pixel()
    {
        // Why the percentile rather than the maximum: one specular highlight would otherwise drag
        // the entire icon down to compensate for it.
        IconBitmap steady = Uniform(10, 10, 0x40);
        IconBitmap withFlare = Uniform(10, 10, 0x40);
        withFlare[9, 9] = new Rgba(255, 255, 255, 255);

        Approx.Relative(
            IconNormalizer.PercentileLuminance(steady),
            IconNormalizer.PercentileLuminance(withFlare),
            1e-12,
            "P90 is unmoved by one stray pixel");

        Assert.NotEqual(Max(steady), Max(withFlare));
    }

    [Fact]
    public void P90_Ignores_How_Much_Padding_An_Icon_Carries()
    {
        // Why the percentile rather than the mean: padding is transparent, and transparent pixels
        // are excluded from the sample entirely.
        IconBitmap tight = Uniform(4, 4, 0x80);

        IconBitmap padded = new(16, 16);
        for (int y = 6; y < 10; y++)
            for (int x = 6; x < 10; x++)
                padded[x, y] = new Rgba(0x80, 0x80, 0x80, 255);

        Approx.Relative(
            IconNormalizer.PercentileLuminance(tight),
            IconNormalizer.PercentileLuminance(padded),
            1e-12,
            "P90 is unmoved by padding");
    }

    [Fact]
    public void Normalise_Lands_P90_On_The_Tier_Target()
    {
        IconBitmap icon = Gradient(16, 16);
        const double target = 0.0331048;                    // the Tier 0 pinned-icon luminance

        IconBitmap normalised = IconNormalizer.Normalise(icon, target);

        Approx.Relative(target, IconNormalizer.PercentileLuminance(normalised), 0.02,
            "P90 after normalisation, within 8-bit quantisation");
    }

    [Fact]
    public void Normalise_Rescues_A_Dark_Logo_And_Tames_A_White_One()
    {
        // §5.1's opening problem: naive desaturation makes a dark logo vanish and a white logo
        // blast. After normalisation both read at the same brightness.
        IconBitmap dark = Uniform(8, 8, 0x12);
        IconBitmap blazing = Uniform(8, 8, 0xFF);
        const double target = 0.0512695;

        double normalisedDark = IconNormalizer.PercentileLuminance(IconNormalizer.Normalise(dark, target));
        double normalisedBright = IconNormalizer.PercentileLuminance(IconNormalizer.Normalise(blazing, target));

        Approx.Relative(target, normalisedDark, 0.05, "the dark logo comes up");
        Approx.Relative(target, normalisedBright, 0.05, "the white logo comes down");
    }

    [Fact]
    public void Alpha_Is_Never_Touched()
    {
        // §5.1 step 5: the silhouette is what carries recognition at this size.
        IconBitmap icon = Gradient(8, 8);
        for (int i = 0; i < icon.Length; i++)
            icon.Pixels[i] = icon.Pixels[i] with { A = (byte)(i * 3 % 256) };

        IconBitmap normalised = IconNormalizer.Normalise(icon, 0.05);

        for (int i = 0; i < icon.Length; i++)
            Assert.Equal(icon.Pixels[i].A, normalised.Pixels[i].A);
    }

    [Fact]
    public void Fully_Transparent_Icons_Do_Not_Divide_By_Zero()
    {
        IconBitmap empty = new(8, 8);

        Assert.Equal(0.0, IconNormalizer.PercentileLuminance(empty));
        Assert.Equal(0.0, IconNormalizer.IntegratedLuminance(IconNormalizer.Normalise(empty, 0.05)));
    }

    [Fact]
    public void P90_Does_Not_Equalise_Emitted_Energy()
    {
        // The withdrawn v1 claim, kept as a test because it is the reason outline mode exists.
        // A solid-fill logo and a thin-stroke logo normalise to identical P90 and still differ by
        // several times in lit area — and therefore in wear.
        IconBitmap solid = Uniform(16, 16, 0x80);
        IconBitmap stroke = Outline(16, 16, 0x80);

        const double target = 0.0331048;
        IconBitmap normalisedSolid = IconNormalizer.Normalise(solid, target);
        IconBitmap normalisedStroke = IconNormalizer.Normalise(stroke, target);

        Approx.Relative(
            IconNormalizer.PercentileLuminance(normalisedSolid),
            IconNormalizer.PercentileLuminance(normalisedStroke),
            0.05,
            "identical peak brightness");

        double solidEnergy = IconNormalizer.IntegratedLuminance(normalisedSolid);
        double strokeEnergy = IconNormalizer.IntegratedLuminance(normalisedStroke);

        Assert.True(solidEnergy > strokeEnergy * 3.0,
            $"P90 equalises peak, not energy — solid {solidEnergy:F4} against stroke {strokeEnergy:F4}.");
    }

    [Fact]
    public void Outline_Mode_Cuts_Lit_Area_Substantially()
    {
        // §5.3: edge-detect, render as a stroke, discard the fill — roughly 70–85% less lit area,
        // which is a reduction in the interior-wear problem rather than a redistribution of it.
        // This measures the ratio the mode has to hit; generating the outline is the shell's job.
        IconBitmap solid = Uniform(16, 16, 0x80);
        IconBitmap outlined = Outline(16, 16, 0x80);

        double reduction = 1.0 - IconNormalizer.IntegratedLuminance(outlined)
                                 / IconNormalizer.IntegratedLuminance(solid);

        Assert.InRange(reduction, 0.70, 0.85);
    }

    [Fact]
    public void Diagnostic_Flags_An_Icon_That_Costs_More_Than_Twice_The_Row_Median()
    {
        // §5.1 step 6, shipped as a diagnostic first: surface "this icon costs 4× the row median"
        // in settings and let the user decide. Automatic dimming of specific logos will look like
        // a rendering bug.
        IconBitmap[] row =
        [
            Outline(16, 16, 0x80),
            Outline(16, 16, 0x80),
            Outline(16, 16, 0x80),
            Uniform(16, 16, 0x80),
        ];

        double median = IconNormalizer.Median(row.Select(IconNormalizer.IntegratedLuminance));

        Assert.False(IconNormalizer.IsFlagged(row[0], median));
        Assert.True(IconNormalizer.IsFlagged(row[3], median));
        Assert.True(IconNormalizer.CostRelativeToRow(row[3], median) > IconNormalizer.DiagnosticThreshold);
    }

    [Fact]
    public void Monochrome_Output_Is_Neutral_And_Preserves_Luminance()
    {
        IconBitmap colour = new(3, 1);
        colour[0, 0] = new Rgba(0xE0, 0x36, 0x4B, 255);     // a saturated red
        colour[1, 0] = new Rgba(0x2F, 0x7D, 0xE1, 255);     // a saturated blue
        colour[2, 0] = new Rgba(0x4B, 0xE0, 0x7A, 255);     // a saturated green

        IconBitmap grey = IconNormalizer.ToMonochrome(colour);

        for (int x = 0; x < 3; x++)
        {
            Rgba pixel = grey[x, 0];

            Assert.Equal(pixel.R, pixel.G);
            Assert.Equal(pixel.G, pixel.B);

            // Same light, no chroma. Within one 8-bit step of the original's luminance.
            Approx.Equal(
                Srgb.RelativeLuminance(colour[x, 0].Rgb),
                Srgb.RelativeLuminance(pixel.Rgb),
                0.005,
                $"luminance of pixel {x} survives desaturation");
        }
    }

    [Fact]
    public void Monochrome_Preserves_Alpha()
    {
        IconBitmap icon = new(2, 1);
        icon[0, 0] = new Rgba(0xE0, 0x36, 0x4B, 128);
        icon[1, 0] = new Rgba(0x2F, 0x7D, 0xE1, 0);

        IconBitmap grey = IconNormalizer.ToMonochrome(icon);

        Assert.Equal(128, grey[0, 0].A);
        Assert.Equal(0, grey[1, 0].A);
    }

    [Fact]
    public void Prepare_Desaturates_Before_Normalising()
    {
        // Order matters: normalising first would land the colour icon's P90 on the target and then
        // move it again when the colours collapse to their luminances.
        IconBitmap colour = new(4, 4);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                colour[x, y] = new Rgba(0xE0, 0x36, 0x4B, 255);

        const double target = 0.0331048;                    // Tier 0, pinned
        IconBitmap prepared = IconNormalizer.Prepare(colour, target, monochrome: true);

        Approx.Relative(target, IconNormalizer.PercentileLuminance(prepared), 0.02,
            "P90 of the prepared icon");

        Rgba pixel = prepared[0, 0];
        Assert.Equal(pixel.R, pixel.G);
        Assert.Equal(pixel.G, pixel.B);
    }

    [Fact]
    public void CapTo_Brings_A_Bright_Icon_Down_And_Keeps_Its_Hue()
    {
        IconBitmap bright = Filled(4, 4, new Rgba(0xF6, 0xE8, 0xC0, 255));   // a pale, bright logo

        Assert.True(IconNormalizer.PercentileLuminance(bright) > TierPalette.Tier2LuminanceCap,
            "Precondition: this icon starts above the cap.");

        IconBitmap capped = IconNormalizer.CapTo(bright, TierPalette.Tier2LuminanceCap);

        Approx.Relative(TierPalette.Tier2LuminanceCap, IconNormalizer.PercentileLuminance(capped), 0.02,
            "P90 lands on the Tier 2 cap");

        // Hue and saturation untouched: scaling happens on all three channels in linear light.
        (double r0, double g0, double b0) = Srgb.ToLinear(bright[0, 0].Rgb);
        (double r1, double g1, double b1) = Srgb.ToLinear(capped[0, 0].Rgb);
        Approx.Relative(r0 / g0, r1 / g1, 0.02, "red-to-green ratio is unchanged");
        Approx.Relative(g0 / b0, g1 / b1, 0.02, "green-to-blue ratio is unchanged");
    }

    [Fact]
    public void CapTo_Leaves_An_Icon_That_Cannot_Reach_The_Cap_Alone()
    {
        // The cap is a ceiling, not a target. A saturated red cannot reach L = 0.45 at all — pure
        // #FF0000 is only L = 0.2126 — and scaling it up would clip the red channel, undershooting
        // the target *and* distorting the hue the cap exists to preserve.
        IconBitmap red = Filled(4, 4, new Rgba(0xE0, 0x36, 0x4B, 255));
        double before = IconNormalizer.PercentileLuminance(red);

        Assert.True(before < TierPalette.Tier2LuminanceCap, "Precondition: it starts below the cap.");

        IconBitmap capped = IconNormalizer.CapTo(red, TierPalette.Tier2LuminanceCap);

        Approx.Relative(before, IconNormalizer.PercentileLuminance(capped), 1e-9, "left untouched");
        Assert.Equal(red[0, 0], capped[0, 0]);
    }

    [Fact]
    public void Saturated_Red_Cannot_Reach_The_Tier_2_Cap_At_All()
    {
        // The gamut fact behind CapTo being downward-only, asserted so nobody "fixes" it later.
        Assert.True(Srgb.RelativeLuminance(Rgb.FromHex("#FF0000")) < TierPalette.Tier2LuminanceCap);
        Approx.Equal(0.2126, Srgb.RelativeLuminance(Rgb.FromHex("#FF0000")), 1e-4, "L(#FF0000)");

        // Scaling it up regardless clips, and the result is neither the target nor the same hue.
        Rgb forced = Srgb.Scale(Rgb.FromHex("#E0364B"), 3.0);
        Assert.True(Srgb.RelativeLuminance(forced) < TierPalette.Tier2LuminanceCap,
            "Clipping means it still never reaches the cap.");
    }

    [Fact]
    public void Tier_Targets_Order_The_Icons_By_Brightness()
    {
        // A running app's icon is one step brighter than a pinned one at every rest tier (§3.1).
        IconBitmap art = Gradient(8, 8);

        double pinnedDormant = Srgb.RelativeLuminance(Rgb.FromHex("#333333"));
        double runningDormant = Srgb.RelativeLuminance(Rgb.FromHex("#404040"));

        double pinned = IconNormalizer.IntegratedLuminance(
            IconNormalizer.Prepare(art, pinnedDormant, monochrome: true));
        double running = IconNormalizer.IntegratedLuminance(
            IconNormalizer.Prepare(art, runningDormant, monochrome: true));

        Assert.True(running > pinned, "The running icon must sit above the pinned one.");
    }

    [Fact]
    public void Median_Handles_Even_And_Odd_Counts()
    {
        Assert.Equal(2.0, IconNormalizer.Median([1.0, 2.0, 3.0]));
        Assert.Equal(2.5, IconNormalizer.Median([1.0, 2.0, 3.0, 4.0]));
        Assert.Equal(0.0, IconNormalizer.Median([]));
    }

    [Fact]
    public void Scaling_Clamps_Rather_Than_Wrapping()
    {
        IconBitmap icon = Uniform(4, 4, 0xF0);

        IconBitmap blown = IconNormalizer.Scale(icon, 100.0);

        foreach (Rgba pixel in blown.Pixels)
            Assert.Equal(255, pixel.R);
    }

    private static IconBitmap Filled(int width, int height, Rgba colour)
    {
        IconBitmap icon = new(width, height);
        for (int i = 0; i < icon.Length; i++) icon.Pixels[i] = colour;
        return icon;
    }

    private static IconBitmap Uniform(int width, int height, byte level)
    {
        IconBitmap icon = new(width, height);
        for (int i = 0; i < icon.Length; i++)
            icon.Pixels[i] = new Rgba(level, level, level, 255);
        return icon;
    }

    /// <summary>A hollow square: a 1 px stroke around the border, transparent inside.</summary>
    private static IconBitmap Outline(int width, int height, byte level)
    {
        IconBitmap icon = new(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                    icon[x, y] = new Rgba(level, level, level, 255);
        return icon;
    }

    private static IconBitmap Gradient(int width, int height)
    {
        IconBitmap icon = new(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte level = (byte)(255 * (y * width + x) / (width * height - 1));
                icon[x, y] = new Rgba(level, level, level, 255);
            }
        return icon;
    }

    private static double Max(IconBitmap icon)
    {
        double max = 0.0;
        foreach (Rgba pixel in icon.Pixels)
            if (pixel.A > 0)
                max = Math.Max(max, Srgb.RelativeLuminance(pixel.Rgb));
        return max;
    }
}
