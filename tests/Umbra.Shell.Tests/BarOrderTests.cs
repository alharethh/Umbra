using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// The row's order — one list covering pinned and running applications alike.
/// </summary>
/// <remarks>
/// This is the class that decides where every icon sits, so it is the one that decides whether the
/// user's layout survives a window opening. The drag interaction on top of it needs a pointer and a
/// live visual tree and is verified by running the bar; the ordering underneath does not, and it is
/// where the failures are silent — a rule that quietly rearranges the row on the next window event
/// looks like the user misremembering where they put something.
/// </remarks>
public class BarOrderTests
{
    private static BarOrder Row(string[] pinned, params string[] running)
    {
        BarOrder order = new();
        order.Reconcile(pinned, running);
        return order;
    }

    private static string[] Keys(BarOrder order) => [.. order.Keys];

    [Fact]
    public void A_Cold_Start_Puts_Pins_First_In_Their_Stored_Order()
    {
        // Nothing is on the bar yet, so everything is an arrival. The pin file's order is what the
        // user arranged, so it is what they get back; whatever else is running follows it.
        BarOrder order = Row(["a", "b"], "c", "a");

        Assert.Equal(["a", "b", "c"], Keys(order));
    }

    [Fact]
    public void A_Newly_Launched_Application_Joins_The_End()
    {
        BarOrder order = Row(["a"], "b");
        order.Reconcile(["a"], ["b", "c"]);

        Assert.Equal(["a", "b", "c"], Keys(order));
    }

    /// <summary>
    /// The row is not segregated: an unpinned icon can sit to the left of a pinned one.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the class. The old layout was <c>pins, then running</c>, which
    /// could not express this arrangement at all — and could not be dragged into it.
    /// </remarks>
    [Fact]
    public void Pinned_And_Unpinned_Mix_Freely()
    {
        BarOrder order = Row(["a", "b"], "c");

        Assert.True(order.Move("c", 0));
        Assert.Equal(["c", "a", "b"], Keys(order));

        // And the arrangement survives the next window event.
        Assert.False(order.Reconcile(["a", "b"], ["c"]));
        Assert.Equal(["c", "a", "b"], Keys(order));
    }

    /// <summary>
    /// Pinning an application leaves it exactly where it was.
    /// </summary>
    /// <remarks>
    /// The bug this replaces: pinning appended to the pin list, and the row drew pins before
    /// running applications, so the icon jumped to the far left the instant it was pinned. Here
    /// pinning changes only which list a key appears in, and the row does not consult that for
    /// position.
    /// </remarks>
    [Fact]
    public void Pinning_Does_Not_Move_The_Icon()
    {
        BarOrder order = Row(["a"], "b", "c");
        Assert.Equal(["a", "b", "c"], Keys(order));

        // The user pins "c", which is running and sitting at the right-hand end.
        Assert.False(order.Reconcile(["a", "c"], ["b", "c"]));
        Assert.Equal(["a", "b", "c"], Keys(order));
    }

    [Fact]
    public void Unpinning_Something_Still_Running_Leaves_It_Where_It_Is()
    {
        BarOrder order = Row(["a", "b", "c"], "b");

        Assert.False(order.Reconcile(["a", "c"], ["b"]));
        Assert.Equal(["a", "b", "c"], Keys(order));
    }

    [Fact]
    public void Unpinning_Something_Not_Running_Takes_It_Off_The_Bar()
    {
        BarOrder order = Row(["a", "b", "c"]);

        Assert.True(order.Reconcile(["a", "c"], []));
        Assert.Equal(["a", "c"], Keys(order));
    }

    [Fact]
    public void A_Pinned_Application_Keeps_Its_Place_When_Its_Last_Window_Closes()
    {
        BarOrder order = Row(["b"], "a", "c");
        order.Move("b", 1);
        Assert.Equal(["a", "b", "c"], Keys(order));

        Assert.False(order.Reconcile(["b"], ["a", "c"]));
        Assert.Equal(["a", "b", "c"], Keys(order));
    }

    /// <summary>
    /// An unpinned application that closes leaves the row, and comes back at the end.
    /// </summary>
    /// <remarks>
    /// This is the question the segregated layout dodged, and the answer is the Windows one. Its
    /// old position is not held: nothing is drawn there to hold, and a row that reserved invisible
    /// gaps for applications that are not running would be a worse surprise than the jump this
    /// replaces. Pinning is how a position is made to stick.
    /// </remarks>
    [Fact]
    public void An_Unpinned_Application_Returns_To_The_End_When_It_Reopens()
    {
        BarOrder order = Row([], "a", "b", "c");
        order.Move("c", 0);
        Assert.Equal(["c", "a", "b"], Keys(order));

        Assert.True(order.Reconcile([], ["a", "b"]));
        Assert.Equal(["a", "b"], Keys(order));

        Assert.True(order.Reconcile([], ["a", "b", "c"]));
        Assert.Equal(["a", "b", "c"], Keys(order));
    }

    [Fact]
    public void Move_Reports_Whether_Anything_Changed()
    {
        BarOrder order = Row([], "a", "b", "c");

        Assert.True(order.Move("a", 2));
        Assert.Equal(["b", "c", "a"], Keys(order));

        Assert.False(order.Move("a", 2));
    }

    [Fact]
    public void Move_Backwards_Works_Too()
    {
        BarOrder order = Row([], "a", "b", "c");

        Assert.True(order.Move("c", 0));
        Assert.Equal(["c", "a", "b"], Keys(order));
    }

    [Fact]
    public void Move_Clamps_Rather_Than_Throwing()
    {
        // A drag runs off both ends of the row routinely; the pointer is not obliged to stay on it.
        BarOrder order = Row([], "a", "b", "c");

        order.Move("a", 99);
        Assert.Equal(["b", "c", "a"], Keys(order));

        order.Move("a", -99);
        Assert.Equal(["a", "b", "c"], Keys(order));
    }

    [Fact]
    public void Moving_Something_That_Is_Not_On_The_Bar_Is_Ignored()
    {
        BarOrder order = Row([], "a", "b");

        Assert.False(order.Move("missing", 0));
        Assert.Equal(["a", "b"], Keys(order));
    }

    [Fact]
    public void Keys_Are_Matched_The_Way_Windows_Matches_Paths()
    {
        // The same executable arrives spelled either way, and a row that treated the two as
        // different applications would draw the icon twice.
        BarOrder order = Row(["C:\\App.exe"], "c:\\app.exe", "C:\\Other.exe");

        Assert.Equal(["C:\\App.exe", "C:\\Other.exe"], Keys(order));
        Assert.Equal(0, order.IndexOf("c:\\APP.exe"));
    }

    [Fact]
    public void PinnedIn_Reads_The_Pin_Order_Out_Of_The_Row()
    {
        // What gets written to the pin file. The running icon between the two pins is not in it,
        // but the fact that "c" now precedes "a" is.
        BarOrder order = Row(["a", "c"], "b");
        order.Move("c", 0);

        Assert.Equal(["c", "a", "b"], Keys(order));
        Assert.Equal(["c", "a"], order.PinnedIn(k => k is "a" or "c"));
    }

    [Fact]
    public void Reconcile_Reports_No_Change_When_There_Is_None()
    {
        // The model writes the pin file when this reports a change, so a false positive puts a disk
        // write on every window that opens anywhere on the desktop.
        BarOrder order = Row(["a"], "b");

        Assert.False(order.Reconcile(["a"], ["b"]));
    }
}
