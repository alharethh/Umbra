using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// A bar-per-monitor set is rebuilt when the monitors change under it, and only then.
/// </summary>
/// <remarks>
/// Rebuilding re-registers every AppBar and reflows every maximised window (§6.3), so the cases
/// that must <em>not</em> rebuild matter as much as the ones that must.
/// </remarks>
public class MonitorTopologyTests
{
    private const string One = @"\\.\DISPLAY1";
    private const string Two = @"\\.\DISPLAY2";
    private const string Three = @"\\.\DISPLAY3";

    private static readonly Display[] TwoScreens = [new(One, true), new(Two, false)];

    [Fact]
    public void The_Same_Monitors_With_The_Same_Primary_Are_Left_Alone()
    {
        Assert.False(MonitorTopology.NeedsRebuild(true, TwoScreens, [new(One, true), new(Two, false)]));
    }

    [Fact]
    public void Enumeration_Order_And_Name_Case_Are_Not_A_Change()
    {
        Display[] reordered = [new(@"\\.\display2", false), new(@"\\.\display1", true)];

        Assert.False(MonitorTopology.NeedsRebuild(true, TwoScreens, reordered));
    }

    /// <summary>
    /// The reported stacking: ManagedShell moves the old primary's bar onto the new primary, where
    /// a bar already is. Same monitors, so only the primary check can catch it.
    /// </summary>
    [Fact]
    public void A_New_Primary_Rebuilds_The_Set()
    {
        Assert.True(MonitorTopology.NeedsRebuild(true, TwoScreens, [new(One, false), new(Two, true)]));
    }

    [Fact]
    public void A_Monitor_Added_Rebuilds_The_Set()
    {
        Assert.True(MonitorTopology.NeedsRebuild(true, TwoScreens, [.. TwoScreens, new(Three, false)]));
    }

    [Fact]
    public void A_Monitor_Removed_Rebuilds_The_Set()
    {
        Assert.True(MonitorTopology.NeedsRebuild(true, TwoScreens, [new(One, true)]));
    }

    /// <summary>
    /// A monitor swapped for another on the same connector count: same count, different screen.
    /// </summary>
    [Fact]
    public void A_Monitor_Replaced_By_Another_Rebuilds_The_Set()
    {
        Assert.True(MonitorTopology.NeedsRebuild(true, TwoScreens, [new(One, true), new(Three, false)]));
    }

    /// <summary>
    /// The second monitor arriving on a machine that started with one is exactly what a set built
    /// for one screen has to follow.
    /// </summary>
    [Fact]
    public void A_Set_Built_On_One_Monitor_Grows_When_A_Second_Arrives()
    {
        Assert.True(MonitorTopology.NeedsRebuild(true, [new(One, true)], TwoScreens));
    }

    /// <summary>
    /// A lone bar follows the primary through ManagedShell, as it always has; rebuilding it would
    /// be a reflow nobody asked for.
    /// </summary>
    [Fact]
    public void A_Lone_Bar_Is_Never_Rebuilt()
    {
        Assert.False(MonitorTopology.NeedsRebuild(false, TwoScreens, [new(One, false), new(Two, true)]));
        Assert.False(MonitorTopology.NeedsRebuild(false, TwoScreens, [new(One, true)]));
    }

    /// <summary>
    /// An enumeration in the middle of a change can come back empty. Rebuilding on it would
    /// restart into a set built for no screens.
    /// </summary>
    [Fact]
    public void An_Empty_Enumeration_Is_Not_A_Reason_To_Rebuild()
    {
        Assert.False(MonitorTopology.NeedsRebuild(true, TwoScreens, []));
    }

    /// <summary>A set whose launch enumeration failed has nothing to compare against.</summary>
    [Fact]
    public void A_Set_Built_For_Unknown_Screens_Is_Not_Rebuilt()
    {
        Assert.False(MonitorTopology.NeedsRebuild(true, [], TwoScreens));
    }

    [Fact]
    public void The_Log_Line_Marks_The_Primary()
    {
        Assert.Equal(@"\\.\DISPLAY1 (primary), \\.\DISPLAY2", MonitorTopology.Describe(TwoScreens));
        Assert.Equal("(none)", MonitorTopology.Describe([]));
    }
}
