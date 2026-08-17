using System.Windows;
using Umbra.Core.Color;
using Umbra.Core.Wear;
using Umbra.Shell.Wear;

namespace Umbra.Shell.Tests;

/// <summary>
/// The rasteriser that turns the drawn scene into per-pixel emitted luminance (design-spec §9).
/// </summary>
public class SceneCompositorTests
{
    [Fact]
    public void A_Solid_Emitter_Fills_Exactly_Its_Rect()
    {
        double[] frame = new double[10 * 4];
        Rgb colour = Rgb.FromHex("#404040");

        SceneCompositor.Composite(
            [new WearEmitter(LuminanceField.Solid(colour), new Rect(2, 1, 4, 2))],
            frame, 10, 4, dpiScale: 1.0);

        double expected = Srgb.RelativeLuminance(colour);

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                bool inside = x >= 2 && x < 6 && y >= 1 && y < 3;
                Assert.Equal(inside ? expected : 0.0, frame[y * 10 + x], 9);
            }
        }
    }

    [Fact]
    public void Black_Costs_Nothing()
    {
        // P1, and the reason the reserved rectangle is mostly free: a black pixel draws no current.
        double[] frame = new double[8 * 2];

        SceneCompositor.Composite(
            [new WearEmitter(LuminanceField.Solid(Rgb.Black), new Rect(0, 0, 8, 2))],
            frame, 8, 2, dpiScale: 1.0);

        Assert.All(frame, cell => Assert.Equal(0.0, cell));
    }

    [Fact]
    public void Dpi_Scale_Converts_Dip_Bounds_To_Physical_Cells()
    {
        double[] frame = new double[20 * 8];

        // 4 × 2 DIP at 125% covers 5 × 2.5 physical pixels.
        SceneCompositor.Composite(
            [new WearEmitter(LuminanceField.Solid(Rgb.White), new Rect(0, 0, 4, 2))],
            frame, 20, 8, dpiScale: 1.25);

        int lit = frame.Count(c => c > 0.0);
        Assert.InRange(lit, 12, 18);           // 5 × 2.5, give or take the edge cells
        Assert.Equal(0.0, frame[19]);          // and nothing beyond it
    }

    [Fact]
    public void Sub_Pixel_Movement_Changes_The_Accumulated_Pattern()
    {
        // The property §11.2 Q1 depends on. Micro-jitter moves content by fractions of a pixel to
        // vary the antialiasing gradient (§6.1); if the compositor quantised that away, the
        // accumulator would report that sub-pixel motion changes nothing, and the experiment
        // designed to settle whether the motion layer is worth shipping would answer "no" by
        // construction.
        // The two offsets are chosen so the emitter covers the *same integer cells* in both cases
        // (floor(left) and ceil(right) are unchanged), leaving the fractional position as the only
        // difference. Without that the test passes for the wrong reason: any shift large enough to
        // cross a pixel boundary also changes the clipped cell range, so even a rasteriser that
        // discarded the fraction entirely would produce two different patterns.
        LuminanceField field = Gradient();

        double[] a = new double[16 * 4];
        double[] b = new double[16 * 4];

        SceneCompositor.Composite([new WearEmitter(field, new Rect(2.1, 1, 6, 2))], a, 16, 4, 1.0);
        SceneCompositor.Composite([new WearEmitter(field, new Rect(2.4, 1, 6, 2))], b, 16, 4, 1.0);

        Assert.False(
            a.Zip(b).All(pair => Math.Abs(pair.First - pair.Second) < 1e-12),
            "A sub-pixel shift within the same cells must still change the sampled pattern.");
    }

    [Fact]
    public void Sub_Pixel_Movement_Spreads_Wear_Rather_Than_Concentrating_It()
    {
        // And the reason it matters: accumulate the same content at a series of fractional offsets
        // and the peak cell must come out below what the same total exposure would burn into one
        // fixed position. That is the entire mechanism the motion layer is betting on.
        LuminanceField field = Gradient();

        WearAccumulator fixedPosition = new(16, 4);
        WearAccumulator jittered = new(16, 4);
        double[] frame = new double[16 * 4];

        // Again confined to one integer cell range, so this measures sub-pixel redistribution and
        // not the emitter sliding onto new pixels.
        for (int step = 0; step < 20; step++)
        {
            SceneCompositor.Composite(
                [new WearEmitter(field, new Rect(2.1, 1, 6, 2))], frame, 16, 4, 1.0);
            fixedPosition.AddFrame(frame, 1.0);

            SceneCompositor.Composite(
                [new WearEmitter(field, new Rect(2.1 + step * 0.02, 1, 6, 2))], frame, 16, 4, 1.0);
            jittered.AddFrame(frame, 1.0);
        }

        Assert.True(jittered.Peak < fixedPosition.Peak,
            $"Jittered peak {jittered.Peak:G6} should sit below fixed {fixedPosition.Peak:G6}.");
    }

    [Fact]
    public void Emitters_Outside_The_Buffer_Are_Clipped_Not_Wrapped()
    {
        double[] frame = new double[8 * 4];

        SceneCompositor.Composite(
            [
                new WearEmitter(LuminanceField.Solid(Rgb.White), new Rect(-10, -10, 4, 4)),
                new WearEmitter(LuminanceField.Solid(Rgb.White), new Rect(100, 100, 4, 4)),
            ],
            frame, 8, 4, dpiScale: 1.0);

        Assert.All(frame, cell => Assert.Equal(0.0, cell));
    }

    [Fact]
    public void Overlapping_Emitters_Take_The_Brighter()
    {
        double[] frame = new double[8 * 2];

        SceneCompositor.Composite(
            [
                new WearEmitter(LuminanceField.Solid(Rgb.FromHex("#404040")), new Rect(0, 0, 8, 2)),
                new WearEmitter(LuminanceField.Solid(Rgb.FromHex("#B3B3B3")), new Rect(2, 0, 2, 2)),
            ],
            frame, 8, 2, dpiScale: 1.0);

        double dim = Srgb.RelativeLuminance(Rgb.FromHex("#404040"));
        double bright = Srgb.RelativeLuminance(Rgb.FromHex("#B3B3B3"));

        Assert.Equal(bright, frame[2], 9);
        Assert.Equal(dim, frame[0], 9);
    }

    [Fact]
    public void The_Frame_Is_Cleared_Between_Composites()
    {
        // Every sample describes the whole scene. A leftover from the previous second would make
        // an icon that has closed keep on wearing the panel.
        double[] frame = new double[8 * 2];

        SceneCompositor.Composite(
            [new WearEmitter(LuminanceField.Solid(Rgb.White), new Rect(0, 0, 8, 2))],
            frame, 8, 2, 1.0);
        Assert.Contains(frame, cell => cell > 0.0);

        SceneCompositor.Composite([], frame, 8, 2, 1.0);
        Assert.All(frame, cell => Assert.Equal(0.0, cell));
    }

    /// <summary>A field that varies across its width, so sub-pixel sampling has something to see.</summary>
    private static LuminanceField Gradient()
    {
        Umbra.Core.Icons.IconBitmap art = new(8, 1);
        for (int x = 0; x < 8; x++)
        {
            byte level = (byte)(32 + x * 28);
            art[x, 0] = new Rgba(level, level, level, 255);
        }

        return LuminanceField.FromIcon(art);
    }
}
