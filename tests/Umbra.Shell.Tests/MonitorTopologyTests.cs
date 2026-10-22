using System.IO;
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

    // ---- What kind of change, and how long it waits ----

    [Fact]
    public void A_Monitor_That_Only_Went_Away_Is_A_Removal()
    {
        Assert.Equal(TopologyChange.RemovedOnly, MonitorTopology.Classify(true, TwoScreens, [new(One, true)]));
    }

    /// <summary>
    /// The primary going away moves its bar onto the new primary, on top of the one there: stacked
    /// at once, so it must not get the removal's grace.
    /// </summary>
    [Fact]
    public void Losing_The_Primary_Is_Not_A_Mere_Removal()
    {
        Assert.Equal(TopologyChange.Changed, MonitorTopology.Classify(true, TwoScreens, [new(Two, true)]));
    }

    [Fact]
    public void An_Arrival_Or_A_Swap_Is_A_Change()
    {
        Assert.Equal(TopologyChange.Changed, MonitorTopology.Classify(true, [new(One, true)], TwoScreens));
        Assert.Equal(TopologyChange.Changed, MonitorTopology.Classify(true, TwoScreens, [new(One, true), new(Three, false)]));
        Assert.Equal(TopologyChange.Changed, MonitorTopology.Classify(true, TwoScreens, [new(One, false), new(Two, true)]));
    }

    /// <summary>
    /// A monitor that blinks out for a few seconds, a sleeping DisplayPort link or a TV checking
    /// its inputs, must be back before a removal is acted on, or every blink is two restarts.
    /// </summary>
    [Fact]
    public void A_Removal_Waits_For_The_Longer_Settle()
    {
        Assert.Equal(TimeSpan.FromSeconds(12),
                     MonitorTopology.StillToSettle(TopologyChange.RemovedOnly, TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.Zero,
                     MonitorTopology.StillToSettle(TopologyChange.RemovedOnly, MonitorTopology.RemovalSettleDelay));
    }

    /// <summary>What the user can see, a screen with no bar or two stacked, is not held back.</summary>
    [Fact]
    public void A_Visible_Change_Does_Not_Wait_Longer()
    {
        Assert.Equal(TimeSpan.Zero, MonitorTopology.StillToSettle(TopologyChange.Changed, TimeSpan.FromSeconds(3)));
    }

    // ---- The rebuild limit ----

    private static readonly DateTime Now = new(2026, 10, 22, 17, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_First_Rebuilds_Are_Allowed()
    {
        Assert.Equal(TimeSpan.Zero, MonitorTopology.RebuildWait([], Now));
        Assert.Equal(TimeSpan.Zero, MonitorTopology.RebuildWait([Now.AddMinutes(-1)], Now));
    }

    /// <summary>
    /// The reviewer's flap: three restarts in 48 s. Two in the window, and the third waits until
    /// the older of them leaves it.
    /// </summary>
    [Fact]
    public void A_Third_Rebuild_Within_The_Window_Waits_For_The_Oldest_To_Leave_It()
    {
        DateTime[] recent = [Now.AddSeconds(-48), Now.AddSeconds(-22)];

        Assert.Equal(MonitorTopology.RebuildWindow - TimeSpan.FromSeconds(48), MonitorTopology.RebuildWait(recent, Now));
    }

    [Fact]
    public void Rebuilds_Older_Than_The_Window_Do_Not_Count()
    {
        DateTime[] recent = [Now - MonitorTopology.RebuildWindow, Now.AddMinutes(-11), Now.AddMinutes(-3)];

        Assert.Equal(TimeSpan.Zero, MonitorTopology.RebuildWait(recent, Now));
    }

    /// <summary>A clock that went backwards must not hold the bar's rebuilds off for however far it jumped.</summary>
    [Fact]
    public void A_Rebuild_Recorded_In_The_Future_Is_Ignored()
    {
        DateTime[] recent = [Now.AddHours(5), Now.AddHours(6)];

        Assert.Equal(TimeSpan.Zero, MonitorTopology.RebuildWait(recent, Now));
    }

    /// <summary>The order on disk does not matter: the wait is from the older of the last two.</summary>
    [Fact]
    public void The_Wait_Does_Not_Depend_On_The_Order_Recorded()
    {
        DateTime[] recent = [Now.AddMinutes(-1), Now.AddMinutes(-8), Now.AddMinutes(-4)];

        Assert.Equal(TimeSpan.FromMinutes(6), MonitorTopology.RebuildWait(recent, Now));
    }

    [Fact]
    public void The_Record_Keeps_The_Window_And_Adds_This_Rebuild()
    {
        DateTime[] recent = [Now.AddMinutes(-30), Now.AddMinutes(-2)];

        Assert.Equal([Now.AddMinutes(-2), Now], MonitorTopology.WithRebuild(recent, Now));
    }

    /// <summary>
    /// The limit has to survive the restart it causes, which is a new process: driven through the
    /// file the way the bar does, a flap is let through twice and then held.
    /// </summary>
    [Fact]
    public void The_Limit_Survives_A_Round_Trip_Through_The_File()
    {
        string path = Path.Combine(Path.GetTempPath(), $"umbra-topology-{Guid.NewGuid():N}");
        try
        {
            DateTime t = Now;
            for (int restart = 0; restart < 2; restart++, t = t.AddSeconds(22))
            {
                IReadOnlyList<DateTime> read = MonitorTopology.ReadRebuilds(path);
                Assert.Equal(TimeSpan.Zero, MonitorTopology.RebuildWait(read, t));
                Assert.True(MonitorTopology.WriteRebuilds(path, MonitorTopology.WithRebuild(read, t)));
            }

            Assert.True(MonitorTopology.RebuildWait(MonitorTopology.ReadRebuilds(path), t) > TimeSpan.Zero);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_Missing_Or_Garbled_Record_Allows_A_Rebuild()
    {
        string path = Path.Combine(Path.GetTempPath(), $"umbra-topology-{Guid.NewGuid():N}");
        Assert.Empty(MonitorTopology.ReadRebuilds(path));

        try
        {
            File.WriteAllText(path, "not a time\n-5\n");
            Assert.Equal(TimeSpan.Zero, MonitorTopology.RebuildWait(MonitorTopology.ReadRebuilds(path), Now));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
