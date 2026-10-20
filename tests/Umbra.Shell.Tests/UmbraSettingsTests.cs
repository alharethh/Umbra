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

        // On by default since 2026-09-20, by deliberate choice: a taskbar
        // replacement without a notification area is not one. It still costs a permanent
        // 100 ms timer, and §11.2 Q2 — whether those timers defeat display idle — is still
        // unmeasured; the default does not settle it.
        Assert.True(settings.Tray);
        Assert.Equal(string.Empty, settings.Monitor);

        // A deviation from Windows 11, recorded in design-spec §8. Windows draws File Explorer's
        // current folder on its taskbar button and offers no way to stop it; measured, two
        // Explorer windows on Downloads and Videos really do answer WM_GETICON with different
        // icons. A taskbar whose icons change as you work is a worse taskbar.
        Assert.True(settings.ApplicationIcons);

        // Off by default: an application that registers itself to start at sign-in without being
        // asked is one people uninstall.
        Assert.False(settings.StartWithWindows);
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
            ApplicationIcons = false,
            StartWithWindows = true,
            Tray = true,
            Monitor = @"\\.\DISPLAY2",
            AppIconHover = AppIconHover.Holographic,
        };

        original.Save(_path);
        UmbraSettings reloaded = UmbraSettings.Load(_path);

        Assert.Equal(original.Edge, reloaded.Edge);
        Assert.Equal(original.Alignment, reloaded.Alignment);
        Assert.Equal(original.DormantIconSize, reloaded.DormantIconSize);
        Assert.Equal(original.Motion, reloaded.Motion);
        Assert.Equal(original.ApplicationIcons, reloaded.ApplicationIcons);
        Assert.Equal(original.StartWithWindows, reloaded.StartWithWindows);
        Assert.Equal(original.Tray, reloaded.Tray);
        Assert.Equal(original.Monitor, reloaded.Monitor);
        Assert.Equal(original.AppIconHover, reloaded.AppIconHover);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("Top, Left")]
    [InlineData("sideways")]
    public void The_Edge_Flag_Ignores_A_Value_That_Is_Not_A_Name(string value)
    {
        Assert.Null(App.ParseEdgeOverride(["--edge", value]));
    }

    [Fact]
    public void The_Edge_Flag_Reads_A_Name()
    {
        Assert.Equal(BarEdge.Left, App.ParseEdgeOverride(["--edge", "left"]));
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

    /// <remarks>
    /// <c>Enum.TryParse</c> reports success for any integer, declared or not, and for comma-joined
    /// names, which it ORs together. <c>edge = 7</c> therefore reached <c>BarMetrics</c> as
    /// <c>(BarEdge)7</c> and threw at startup, after Explorer's taskbar had been suppressed, on
    /// every launch. A declared number is refused as well: Umbra only ever writes names, so a
    /// number in the file was not written by it, and "1" meaning Top is a coincidence of
    /// declaration order rather than a format. Each enum key is checked, because each had its own
    /// <c>TryParse</c> and fixing one says nothing about the others.
    /// </remarks>
    [Theory]
    [InlineData("edge = 7")]
    [InlineData("edge = -1")]
    [InlineData("edge = 1")]
    [InlineData("edge = Top, Left")]
    public void A_Number_Or_Combination_Is_Not_An_Edge(string line)
    {
        Assert.Equal(BarEdge.Bottom, Written(line).Edge);
    }

    [Theory]
    [InlineData("alignment = 42")]
    [InlineData("alignment = 1")]
    [InlineData("alignment = Start, Center")]
    public void A_Number_Or_Combination_Is_Not_An_Alignment(string line)
    {
        Assert.Equal(Alignment.Start, Written(line).Alignment);
    }

    [Theory]
    [InlineData("appIconHover = 9")]
    [InlineData("appIconHover = 1")]
    public void A_Number_Is_Not_A_Hover_Treatment(string line)
    {
        Assert.Equal(new UmbraSettings().AppIconHover, Written(line).AppIconHover);
    }

    /// <summary>
    /// The QA reproduction exactly: three out-of-range values in one file, and every declared
    /// value of every key still has to come out as something the bar can lay out.
    /// </summary>
    [Fact]
    public void The_QA_File_Yields_Only_Declared_Values()
    {
        UmbraSettings settings = Written("edge = 7", "alignment = 42", "appIconHover = 9");

        Assert.True(Enum.IsDefined(settings.Edge));
        Assert.True(Enum.IsDefined(settings.Alignment));
        Assert.True(Enum.IsDefined(settings.AppIconHover));
    }

    [Theory]
    [InlineData("edge = left", BarEdge.Left)]
    [InlineData("edge = RIGHT", BarEdge.Right)]
    [InlineData("edge =   Top  ", BarEdge.Top)]
    public void Names_Still_Parse_In_Any_Case(string line, BarEdge expected)
    {
        Assert.Equal(expected, Written(line).Edge);
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
        // Switched *off* here, because the default is now on and a comparison against an
        // identical value would pass whatever this method did.
        Assert.True(baseline.NeedsRestartToReach(new UmbraSettings { Tray = false }));

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
