using Umbra.Core.Icons;

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
