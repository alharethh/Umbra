using Umbra.Core.Color;

namespace Umbra.Core.Tests;

/// <summary>
/// A theme's tint for the bar's greys: a hue at the same luminance, so the same wear.
/// </summary>
public class RestingTintTests
{
    private static readonly string[] Greys = ["#333333", "#404040", "#4D4D4D", "#666666"];

    private static readonly string[] Picks =
        ["#8A93A6", "#A69080", "#8FA68F", "#9A8FAD", "#AD8F99", "#0000FF", "#FF0000", "#00FF00", "#FFFF00"];

    [Theory]
    [InlineData("#808080")]
    [InlineData("#404040")]
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    public void A_Grey_Or_Black_Is_No_Tint(string hex) =>
        Assert.True(RestingTint.Toward(Rgb.FromHex(hex)).IsNone);

    [Fact]
    public void No_Tint_Draws_The_Greys_Unchanged()
    {
        foreach (string grey in Greys)
            Assert.Equal(Rgb.FromHex(grey), RestingTint.None.Apply(Rgb.FromHex(grey)));
    }

    /// <summary>
    /// The point of the whole thing. To 8-bit quantisation: one code value at these levels is a
    /// few thousandths of luminance, and a tint that added light would be caught well above that.
    /// </summary>
    [Fact]
    public void A_Tinted_Grey_Has_The_Greys_Luminance()
    {
        foreach (string pick in Picks)
        {
            RestingTint tint = RestingTint.Toward(Rgb.FromHex(pick));
            Assert.False(tint.IsNone, pick);

            foreach (string grey in Greys)
            {
                double before = Srgb.RelativeLuminance(Rgb.FromHex(grey));
                double after = Srgb.RelativeLuminance(tint.Apply(Rgb.FromHex(grey)));

                Assert.True(Math.Abs(after - before) <= 0.0015,
                    $"{pick} on {grey}: L {before:F4} -> {after:F4}");
            }
        }
    }

    [Fact]
    public void The_Multiplier_Itself_Has_Luminance_One()
    {
        foreach (string pick in Picks)
        {
            RestingTint t = RestingTint.Toward(Rgb.FromHex(pick));
            Assert.Equal(1.0, Srgb.Kr * t.R + Srgb.Kg * t.G + Srgb.Kb * t.B, 9);
        }
    }

    /// <summary>
    /// Pure blue at equal luminance would drive the blue subpixel at fourteen times the grey's.
    /// The cap turns a vivid pick into a gentle tint of the same hue instead.
    /// </summary>
    [Fact]
    public void No_Channel_Is_Driven_Past_The_Gain_Limit()
    {
        foreach (string pick in Picks)
        {
            RestingTint t = RestingTint.Toward(Rgb.FromHex(pick));
            Assert.True(Math.Max(t.R, Math.Max(t.G, t.B)) <= RestingTint.MaxChannelGain + 1e-9, pick);
            Assert.True(Math.Min(t.R, Math.Min(t.G, t.B)) >= 0.0, pick);
        }

        RestingTint blue = RestingTint.Toward(Rgb.FromHex("#0000FF"));
        Assert.Equal(RestingTint.MaxChannelGain, blue.B, 9);
        Assert.True(blue.B > blue.R && blue.B > blue.G);
    }

    [Fact]
    public void Brightness_Of_The_Pick_Does_Not_Matter_Only_Its_Hue()
    {
        RestingTint dim = RestingTint.Toward(Rgb.FromHex("#202040"));
        RestingTint bright = RestingTint.Toward(Rgb.FromHex("#404080"));

        Assert.Equal(dim.R, bright.R, 2);
        Assert.Equal(dim.B, bright.B, 2);
    }
}
