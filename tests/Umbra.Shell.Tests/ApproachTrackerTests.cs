using System.Windows;
using Umbra.Shell.Picker;

namespace Umbra.Shell.Tests;

/// <summary>
/// The safe triangle that keeps a preview open while the pointer crosses neighbouring icons on its
/// way to a thumbnail.
/// </summary>
/// <remarks>
/// Laid out from a real log line: File Explorer's two-window picker at 4,924 450×172 DIP over a
/// bar whose icons sit at y ≈ 1128, Explorer's slot at x ≈ 121 and Discord's beside it at ≈ 165.
/// The second half matters as much as the first — a tracker that always said "approaching" would
/// fix the report and break scrubbing along the row, which is how the Windows taskbar is used.
/// </remarks>
public class ApproachTrackerTests
{
    private static readonly Rect Picker = new(4, 924, 450, 172);

    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);

    private static TimeSpan Ms(int ms) => T0 + TimeSpan.FromMilliseconds(ms);

    private static ApproachTracker From(Point at, int ms = 0)
    {
        ApproachTracker tracker = new();
        tracker.Add(at, Ms(ms));
        return tracker;
    }

    [Fact]
    public void Heading_Up_And_Across_To_A_Thumbnail_Is_Approaching()
    {
        // Off Explorer's icon, up and to the right, over Discord on the way to the second thumbnail.
        ApproachTracker tracker = From(new Point(121, 1130));

        Assert.True(tracker.IsApproaching(Picker, new Point(160, 1112), Ms(100)));
    }

    [Fact]
    public void Sliding_Along_The_Row_Under_The_Picker_Is_Not()
    {
        ApproachTracker tracker = From(new Point(121, 1128));

        Assert.False(tracker.IsApproaching(Picker, new Point(165, 1128), Ms(100)));
    }

    [Fact]
    public void Coming_To_Rest_On_The_Neighbour_Is_Not()
    {
        // Two pixels of drift straight towards the picker over a quarter of a second: inside the
        // triangle, and resting.
        ApproachTracker tracker = From(new Point(160, 1112));
        tracker.Add(new Point(160, 1111), Ms(120));

        Assert.False(tracker.IsApproaching(Picker, new Point(160, 1110), Ms(240)));
    }

    /// <summary>
    /// About 16 DIP/s straight up from Discord's icon towards the picker.
    /// </summary>
    /// <remarks>
    /// Judged over the 90 ms the direction is read from, this moves under a pixel and a half and
    /// counts as a stop — which released the held switch and flipped the preview to Discord's and
    /// back, every few hundred milliseconds, for anyone moving carefully. Seen in the log on
    /// hardware before it was understood.
    /// </remarks>
    [Fact]
    public void A_Slow_Deliberate_Approach_Is_Still_An_Approach()
    {
        ApproachTracker tracker = new();
        for (int i = 0; i <= 4; i++) tracker.Add(new Point(165, 1130 - 0.8 * i), Ms(50 * i));

        Assert.True(tracker.IsApproaching(Picker, new Point(165, 1130 - 0.8 * 5), Ms(250)));
    }

    [Fact]
    public void Moving_Away_Is_Not_Whatever_The_Angle()
    {
        ApproachTracker tracker = From(new Point(160, 1112));

        Assert.False(tracker.IsApproaching(Picker, new Point(170, 1130), Ms(100)));
    }

    [Fact]
    public void Heading_Past_The_Picker_Is_Not()
    {
        // Getting closer, but aimed outside the picker's right-hand corner.
        ApproachTracker tracker = From(new Point(420, 1128));

        Assert.False(tracker.IsApproaching(Picker, new Point(470, 1110), Ms(100)));
    }

    [Fact]
    public void On_Or_Just_Beside_The_Picker_Is_Approaching_With_No_History()
    {
        ApproachTracker tracker = new();

        Assert.True(tracker.IsApproaching(Picker, new Point(200, 1000), Ms(0)));
        Assert.True(tracker.IsApproaching(Picker, new Point(200, 1100), Ms(0)));
    }

    /// <summary>
    /// The direction comes from the last ~90 ms of the path, not from the oldest sample kept.
    /// </summary>
    /// <remarks>
    /// Out along the picker's lower edge, back down onto the row, then up towards it. Read from
    /// where the pointer started, the last leg points away from the panel; read from where it
    /// turned, it points straight at it.
    /// </remarks>
    [Fact]
    public void A_Change_Of_Direction_Is_Read_From_The_Recent_Path()
    {
        ApproachTracker tracker = From(new Point(300, 1110));
        tracker.Add(new Point(160, 1128), Ms(180));

        Assert.True(tracker.IsApproaching(Picker, new Point(150, 1112), Ms(290)));
    }

    [Fact]
    public void Nothing_Is_Inferred_From_Stale_History()
    {
        ApproachTracker tracker = From(new Point(121, 1130));

        Assert.False(tracker.IsApproaching(Picker, new Point(160, 1112), Ms(1000)));
    }
}
