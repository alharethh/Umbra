using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Umbra.Shell.Rendering;

namespace Umbra.Shell.Tests;

/// <summary>
/// The row runs away from Start on every edge: left to right along a horizontal bar, bottom to top
/// up a vertical one, where Start sits in the bottom corner (§7.3).
/// </summary>
/// <remarks>
/// A vertical row used to stack top-down while aligned to the bottom, so the first icon was the
/// one furthest from Start, a newly opened application landed beside Start, and every icon
/// already there was pushed up a slot when it did. These arrange a real panel and read back where
/// each icon went, and then check that the index arithmetic the drag and the file drop use agrees
/// with the arrangement — a panel reversed without them would drop icons into the mirror-image
/// slot of the one under the pointer.
/// </remarks>
public class RowOrderTests
{
    private const double Pitch = 44;

    [Fact]
    public void A_Vertical_Row_Starts_Beside_Start_In_The_Bottom_Corner()
    {
        OnSta(() =>
        {
            double[] at = Arrange(Orientation.Vertical, fromEnd: true, count: 3);

            // Start is below the row, so the first icon is the lowest one.
            Assert.Equal(2 * Pitch, at[0]);
            Assert.Equal(1 * Pitch, at[1]);
            Assert.Equal(0, at[2]);
        });
    }

    [Fact]
    public void A_New_Icon_On_A_Vertical_Row_Moves_None_Already_There()
    {
        OnSta(() =>
        {
            // The row is bottom-aligned, so what matters is each icon's distance from the bottom.
            double[] before = FromBottom(Arrange(Orientation.Vertical, fromEnd: true, count: 3), 3);
            double[] after = FromBottom(Arrange(Orientation.Vertical, fromEnd: true, count: 4), 4);

            for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], after[i]);
        });
    }

    [Fact]
    public void A_Horizontal_Row_Is_Unchanged()
    {
        OnSta(() =>
        {
            double[] at = Arrange(Orientation.Horizontal, fromEnd: false, count: 3);

            Assert.Equal([0, Pitch, 2 * Pitch], at);
        });
    }

    [Theory]
    [InlineData(Orientation.Horizontal, false)]
    [InlineData(Orientation.Vertical, true)]
    public void The_Drag_Picks_The_Slot_Under_The_Pointer(Orientation orientation, bool fromEnd)
    {
        OnSta(() =>
        {
            const int count = 4;
            double[] at = Arrange(orientation, fromEnd, count);

            for (int i = 0; i < count; i++)
            {
                // A quarter of the way into the slot the panel put icon i in.
                Assert.Equal(i, SlidingStackPanel.SlotAt(at[i] + Pitch / 4, Pitch, count, fromEnd));
            }
        });
    }

    [Fact]
    public void The_Drag_Clamps_To_The_Row()
    {
        // Past Start (below the row) is the first slot; past the far end is the last.
        Assert.Equal(0, SlidingStackPanel.SlotAt(4 * Pitch + 30, Pitch, 4, fromEnd: true));
        Assert.Equal(3, SlidingStackPanel.SlotAt(-30, Pitch, 4, fromEnd: true));
    }

    [Fact]
    public void A_File_Dropped_On_A_Vertical_Row_Lands_In_The_Gap_Under_The_Pointer()
    {
        OnSta(() =>
        {
            const int count = 3;
            double[] at = Arrange(Orientation.Vertical, fromEnd: true, count);

            // Just below icon 1's bottom edge is the gap between icons 0 and 1, which is gap 1.
            Assert.Equal(1, SlidingStackPanel.GapAt(at[1] + Pitch + 3, Pitch, count, fromEnd: true));

            // Above the whole row — the empty run of bar towards the clock — is the end.
            Assert.Equal(count, SlidingStackPanel.GapAt(-200, Pitch, count, fromEnd: true));

            // On Start, below the row, is the beginning.
            Assert.Equal(0, SlidingStackPanel.GapAt(count * Pitch + 20, Pitch, count, fromEnd: true));
        });
    }

    /// <summary>Lays out <paramref name="count"/> slot-sized children and returns where each went.</summary>
    private static double[] Arrange(Orientation orientation, bool fromEnd, int count)
    {
        var panel = new SlidingStackPanel { Orientation = orientation, FromEnd = fromEnd };
        for (int i = 0; i < count; i++)
        {
            panel.Children.Add(new Border { Width = Pitch, Height = Pitch });
        }

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));

        return panel.Children.Cast<UIElement>().Select(SlidingStackPanel.ArrangedAt).ToArray();
    }

    private static double[] FromBottom(double[] at, int count) =>
        at.Select(a => count * Pitch - a).ToArray();

    private static void OnSta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
