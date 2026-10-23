using Umbra.Core.Wear;

namespace Umbra.Core.Tests;

/// <summary>
/// The heatmap's colouring, shared by the window, its saved picture and the README tool.
/// </summary>
public class HeatmapPixelsTests
{
    [Fact]
    public void The_Ramp_Is_The_Readme_Pictures()
    {
        // The stops the published image was drawn with. The window used to carry a different set,
        // so a user's saved picture would not have matched the README's.
        Assert.Equal(((byte)0, (byte)0, (byte)0), HeatmapPixels.Ramp(0.0));
        Assert.Equal(((byte)20, (byte)24, (byte)90), HeatmapPixels.Ramp(0.25));
        Assert.Equal(((byte)150, (byte)30, (byte)120), HeatmapPixels.Ramp(0.5));
        Assert.Equal(((byte)240, (byte)120, (byte)40), HeatmapPixels.Ramp(0.75));
        Assert.Equal(((byte)255, (byte)255, (byte)235), HeatmapPixels.Ramp(1.0));
    }

    [Fact]
    public void Peak_Is_The_Top_Of_The_Ramp_And_Below_The_Floor_Is_Black()
    {
        WearAccumulator wear = new(4, 1);
        wear.Add(0, 0, 1.0, 10);
        wear.Add(1, 0, 1e-5, 10);   // (1e-5)^1.7 is about 3e-9 of the peak: below the six-decade floor
        wear.Add(2, 0, 0.1, 10);

        byte[] px = HeatmapPixels.Bgra(wear);

        Assert.Equal(new byte[] { 235, 255, 255, 255 }, px[0..4]);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, px[4..8]);
        Assert.NotEqual(0, px[8] + px[9] + px[10]);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, px[12..16]);
    }

    [Fact]
    public void An_Empty_Dataset_Is_Black_And_Opaque()
    {
        byte[] px = HeatmapPixels.Bgra(new WearAccumulator(3, 2));

        Assert.Equal(24, px.Length);
        for (int i = 0; i < px.Length; i += 4)
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, px[i..(i + 4)]);
    }
}
