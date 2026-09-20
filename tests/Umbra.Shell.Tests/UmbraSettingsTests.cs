using System.IO;
using Umbra.Core.Layout;
using Umbra.Shell.Settings;

namespace Umbra.Shell.Tests;

/// <summary>
/// The settings file: what it defaults to, what it does with a damaged value, and whether what is
/// written can be read back.
/// </summary>
/// <remarks>
/// The failure this guards is quiet. A round-trip that loses a field, or a parser that silently
/// swallows a value it does not understand, leaves the user with a bar that is not the one they
/// configured and no indication why.
/// </remarks>
public class UmbraSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"umbra-settings-{Guid.NewGuid():N}.txt");

    private UmbraSettings Written(params string[] lines)
    {
        File.WriteAllLines(_path, lines);
        return UmbraSettings.Load(_path);
    }

    [Fact]
    public void Defaults_Are_The_Spec_Defaults()
    {
        UmbraSettings settings = new();

        // §7.1: bottom edge, left-aligned row.
        Assert.Equal(BarEdge.Bottom, settings.Edge);
        Assert.Equal(Alignment.Start, settings.Alignment);

        // A deliberate deviation from §7.1's 24 px: the bar ships with idle icons shrunk, and 16 is
        // the smallest size that still reads as a taskbar while actually cooling the hottest pixel.
        Assert.Equal(16.0, settings.DormantIconSize);
        Assert.True(settings.DormantIconSize < SlotMetrics.BaseIconExtent,
            "The dormant size must be below the full size, or nothing shrinks.");
        Assert.True(settings.Motion);

        // Works, but costs a permanent 100 ms timer, so it is off until asked for.
        Assert.False(settings.Tray);
        Assert.Equal(string.Empty, settings.Monitor);
    }

    [Fact]
    public void A_Missing_File_Yields_Defaults_Rather_Than_Throwing()
    {
        UmbraSettings settings = UmbraSettings.Load(
            Path.Combine(Path.GetTempPath(), $"umbra-absent-{Guid.NewGuid():N}.txt"));

        Assert.Equal(BarEdge.Bottom, settings.Edge);
        Assert.True(settings.Motion);
    }

    [Fact]
    public void Every_Field_Survives_A_Round_Trip()
    {
        UmbraSettings original = new()
        {
            Edge = BarEdge.Left,
            Alignment = Alignment.Center,
            DormantIconSize = 16,
            Motion = false,
            Tray = true,
            Monitor = @"\\.\DISPLAY2",
        };

        original.Save(_path);
        UmbraSettings reloaded = UmbraSettings.Load(_path);

        Assert.Equal(original.Edge, reloaded.Edge);
        Assert.Equal(original.Alignment, reloaded.Alignment);
        Assert.Equal(original.DormantIconSize, reloaded.DormantIconSize);
        Assert.Equal(original.Motion, reloaded.Motion);
        Assert.Equal(original.Tray, reloaded.Tray);
        Assert.Equal(original.Monitor, reloaded.Monitor);
    }

    [Fact]
    public void Keys_Are_Case_Insensitive_And_Whitespace_Tolerant()
    {
        UmbraSettings settings = Written(
            "   EDGE   =   Top   ",
            "MOTION=false",
            "  # a comment",
            string.Empty,
            "DormantIconSize = 18");

        Assert.Equal(BarEdge.Top, settings.Edge);
        Assert.False(settings.Motion);
        Assert.Equal(18, settings.DormantIconSize);
    }

    /// <summary>
    /// A damaged value must leave the default in place rather than taking a zero.
    /// </summary>
    /// <remarks>
    /// An icon extent of 0 draws nothing at all, and a negative one throws out of
    /// <see cref="SlotMetrics"/> — either would turn a typo in a hand-edited file into a bar that
    /// does not come up.
    /// </remarks>
    [Theory]
    [InlineData("dormantIconSize = 0")]
    [InlineData("dormantIconSize = -4")]
    [InlineData("dormantIconSize = enormous")]
    [InlineData("dormantIconSize = 500")]
    public void An_Unusable_Icon_Size_Keeps_The_Default(string line)
    {
        Assert.Equal(new UmbraSettings().DormantIconSize, Written(line).DormantIconSize);
    }

    [Fact]
    public void An_Unusable_Edge_Keeps_The_Default()
    {
        Assert.Equal(BarEdge.Bottom, Written("edge = sideways").Edge);
    }

    [Fact]
    public void Unknown_Keys_Are_Ignored_Rather_Than_Fatal()
    {
        UmbraSettings settings = Written("edge = Right", "somethingElse = 12", "motion = false");

        Assert.Equal(BarEdge.Right, settings.Edge);
        Assert.False(settings.Motion);
    }

    [Fact]
    public void The_Written_File_Reads_Back_Through_Its_Own_Parser()
    {
        // Serialise() carries comments and blank lines; Load() has to cope with its own output.
        UmbraSettings original = new() { Edge = BarEdge.Right, DormantIconSize = 14, Tray = true };
        File.WriteAllLines(_path, original.Serialise());

        UmbraSettings reloaded = UmbraSettings.Load(_path);

        Assert.Equal(BarEdge.Right, reloaded.Edge);
        Assert.Equal(14, reloaded.DormantIconSize);
        Assert.True(reloaded.Tray);
    }

    [Fact]
    public void Clone_Is_Independent_So_A_Cancelled_Edit_Changes_Nothing()
    {
        UmbraSettings original = new() { Edge = BarEdge.Bottom, DormantIconSize = 24 };
        UmbraSettings copy = original.Clone();

        copy.Edge = BarEdge.Top;
        copy.DormantIconSize = 12;

        Assert.Equal(BarEdge.Bottom, original.Edge);
        Assert.Equal(24, original.DormantIconSize);
    }

    /// <summary>
    /// Which changes need the bar rebuilt. Getting this wrong either restarts needlessly or, worse,
    /// leaves the user staring at a setting that appears to have applied and has not.
    /// </summary>
    [Fact]
    public void Only_Registration_Bound_Settings_Need_A_Restart()
    {
        UmbraSettings baseline = new();

        // Tray hosting is the only one left: it is built into the shell host at startup, and
        // unlike the placement settings there is no live path for it.
        Assert.True(baseline.NeedsRestartToReach(new UmbraSettings { Tray = true }));

        // Properties of what is drawn.
        Assert.False(baseline.NeedsRestartToReach(new UmbraSettings { DormantIconSize = 16 }));
        Assert.False(baseline.NeedsRestartToReach(new UmbraSettings { Motion = false }));
        Assert.False(baseline.NeedsRestartToReach(baseline.Clone()));

        // Alignment moves the icons inside the reserved strip without changing the strip, so it
        // never touches the AppBar registration at all.
        Assert.False(baseline.NeedsRestartToReach(new UmbraSettings { Alignment = Alignment.Center }));

        // Edge and monitor do re-register it, and that is allowed: \u00a76.3 forbids re-registering in
        // steady state because it reflows every maximised window, but a user deliberately moving
        // the bar is not steady state and the reflow is what they asked for.
        Assert.False(baseline.NeedsRestartToReach(new UmbraSettings { Edge = BarEdge.Top }));
        Assert.False(baseline.NeedsRestartToReach(new UmbraSettings { Monitor = @"\\.\DISPLAY2" }));
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }
}
