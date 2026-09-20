using System.IO;
using Umbra.Shell.Settings;

namespace Umbra.Shell.Tests;

/// <summary>
/// The hover settings: an accent colour, a choice of treatment for the application row, and a
/// colour for the plain chip.
/// </summary>
/// <remarks>
/// The colour ones are stored as text a person may well type by hand, so the reader has to decide
/// what a colour is. Getting that wrong is not a wrong colour — <c>ColorConverter</c> throws, and
/// the throw would land on the way to the bar's first paint.
/// </remarks>
public class HoverSettingsTests
{
    private static UmbraSettings Parse(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"umbra-hover-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllLines(path, lines);

            return UmbraSettings.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_Defaults_Are_The_Shipping_Hover()
    {
        UmbraSettings settings = new();

        Assert.Equal(UmbraSettings.DefaultHoverAccent, settings.HoverAccent);
        Assert.Equal(UmbraSettings.DefaultAppIconHoverColour, settings.AppIconHoverColour);

        // The row keeps its plain chip unless asked otherwise: it is the cheaper of the two by a
        // wide margin, and a default that lights more of the panel should be opted into.
        Assert.Equal(AppIconHover.Chip, settings.AppIconHover);
    }

    [Fact]
    public void Colours_And_The_Row_Treatment_Round_Trip()
    {
        UmbraSettings settings = new()
        {
            HoverAccent = "#B69CFF",
            AppIconHover = AppIconHover.Holographic,
            AppIconHoverColour = "#FF203040",
        };

        UmbraSettings read = Parse([.. settings.Serialise()]);

        Assert.Equal("#B69CFF", read.HoverAccent);
        Assert.Equal(AppIconHover.Holographic, read.AppIconHover);
        Assert.Equal("#FF203040", read.AppIconHoverColour);
    }

    /// <summary>Both spellings, because one of them is going to be typed.</summary>
    [Theory]
    [InlineData("appIconHoverColour")]
    [InlineData("appIconHoverColor")]
    public void Either_Spelling_Of_Colour_Is_Read(string key) =>
        Assert.Equal("#123456", Parse($"{key} = #123456").AppIconHoverColour);

    /// <summary>
    /// A value that is not a colour leaves the default in place rather than reaching the renderer.
    /// </summary>
    /// <remarks>
    /// Stated as several kinds of wrong, because they fail differently: the wrong length parses as
    /// a shorthand nobody meant, a missing hash is a named colour to some parsers, and a non-hex
    /// digit is the ordinary typo.
    /// </remarks>
    [Theory]
    [InlineData("cyan")]
    [InlineData("#53E7F")]
    [InlineData("53E7FF")]
    [InlineData("#53E7FG")]
    [InlineData("")]
    public void Anything_That_Is_Not_A_Colour_Keeps_The_Default(string value) =>
        Assert.Equal(
            UmbraSettings.DefaultHoverAccent,
            Parse($"hoverAccent = {value}").HoverAccent);

    /// <summary>Eight digits is a colour too — the alpha form is what WPF writes.</summary>
    [Fact]
    public void An_Alpha_Prefixed_Colour_Is_Accepted() =>
        Assert.Equal("#FF53E7FF", Parse("hoverAccent = #FF53E7FF").HoverAccent);

    /// <summary>An unusable treatment keeps the row as it was.</summary>
    [Fact]
    public void An_Unknown_Row_Treatment_Keeps_The_Default() =>
        Assert.Equal(AppIconHover.Chip, Parse("appIconHover = sparkles").AppIconHover);

    /// <summary>
    /// None of this needs a restart.
    /// </summary>
    /// <remarks>
    /// All three only change what is drawn, and the row builds both treatments for every slot so
    /// switching is a visibility flip rather than a retemplate. Tray hosting is still the only
    /// setting that cannot be applied live.
    /// </remarks>
    [Fact]
    public void Hover_Changes_Apply_Without_A_Restart()
    {
        UmbraSettings baseline = new();

        Assert.False(baseline.NeedsRestartToReach(new UmbraSettings { HoverAccent = "#B69CFF" }));
        Assert.False(baseline.NeedsRestartToReach(new UmbraSettings { AppIconHoverColour = "#112233" }));
        Assert.False(baseline.NeedsRestartToReach(
            new UmbraSettings { AppIconHover = AppIconHover.Holographic }));
    }

    /// <summary>A clone carries them, or cancelling a settings window would lose the choice.</summary>
    [Fact]
    public void The_Working_Copy_Carries_Them()
    {
        UmbraSettings settings = new()
        {
            HoverAccent = "#6BE39A",
            AppIconHover = AppIconHover.Holographic,
            AppIconHoverColour = "#FF010203",
        };

        UmbraSettings clone = settings.Clone();

        Assert.Equal(settings.HoverAccent, clone.HoverAccent);
        Assert.Equal(settings.AppIconHover, clone.AppIconHover);
        Assert.Equal(settings.AppIconHoverColour, clone.AppIconHoverColour);
    }
}
