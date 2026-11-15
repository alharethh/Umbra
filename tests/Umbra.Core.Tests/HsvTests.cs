using Umbra.Core.Color;

namespace Umbra.Core.Tests;

/// <summary>The colour picker's axes: hue, saturation and value, to and from sRGB bytes.</summary>
public class HsvTests
{
    [Theory]
    [InlineData("#FF0000", 0, 1, 1)]
    [InlineData("#00FF00", 120, 1, 1)]
    [InlineData("#0000FF", 240, 1, 1)]
    [InlineData("#FFFF00", 60, 1, 1)]
    [InlineData("#FF00FF", 300, 1, 1)]
    [InlineData("#808080", 0, 0, 0.50196)]
    [InlineData("#000000", 0, 0, 0)]
    [InlineData("#53E7FF", 188.37, 0.67451, 1)]
    public void Known_Colours_Have_Known_Axes(string hex, double h, double s, double v)
    {
        Hsv hsv = Hsv.FromRgb(Rgb.FromHex(hex));
        Assert.Equal(h, hsv.H, 2);
        Assert.Equal(s, hsv.S, 4);
        Assert.Equal(v, hsv.V, 4);
    }

    [Fact]
    public void Every_Byte_Colour_On_A_Coarse_Grid_Survives_A_Round_Trip()
    {
        // Steps of 17 cover every hue sextant, the greys and both ends of each channel.
        for (int r = 0; r <= 255; r += 17)
            for (int g = 0; g <= 255; g += 17)
                for (int b = 0; b <= 255; b += 17)
                {
                    Rgb colour = new((byte)r, (byte)g, (byte)b);
                    Assert.Equal(colour, Hsv.FromRgb(colour).ToRgb());
                }
    }

    [Fact]
    public void Hue_Wraps_And_Out_Of_Range_Axes_Are_Clamped()
    {
        Assert.Equal(new Hsv(0, 1, 1).ToRgb(), new Hsv(360, 1, 1).ToRgb());
        Assert.Equal(new Hsv(300, 1, 1).ToRgb(), new Hsv(-60, 1, 1).ToRgb());
        Assert.Equal(Rgb.White, new Hsv(10, -0.5, 1.5).ToRgb());
    }
}
