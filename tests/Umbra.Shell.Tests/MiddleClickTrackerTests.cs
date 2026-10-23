using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// A middle release launches only over the icon the same press began on. Each test replays the
/// events in the order the bar raises them: a press is reported by the window's tunnelling handler
/// with no target and then by the icon's own handler; a release by the icon's handler and then by
/// the window's, which sees handled events too.
/// </summary>
public class MiddleClickTrackerTests
{
    private sealed class Slot;

    private readonly MiddleClickTracker<Slot> _tracker = new();
    private readonly Slot _chrome = new();
    private readonly Slot _firefox = new();

    private void PressOn(Slot? slot)
    {
        _tracker.Press(null);
        if (slot is not null) _tracker.Press(slot);
    }

    private bool ReleaseOn(Slot? slot)
    {
        bool launched = slot is not null && _tracker.Release(slot);
        _tracker.Release(null);
        return launched;
    }

    [Fact]
    public void A_Press_And_Release_On_The_Same_Icon_Launches()
    {
        PressOn(_chrome);
        Assert.True(ReleaseOn(_chrome));
    }

    [Fact]
    public void Sliding_Off_To_Release_Elsewhere_Cancels()
    {
        PressOn(_chrome);
        Assert.False(ReleaseOn(null));
    }

    [Fact]
    public void Sliding_Onto_Another_Icon_Cancels()
    {
        PressOn(_chrome);
        Assert.False(ReleaseOn(_firefox));
    }

    // The review's repro for PR #20: the cancelled press must not survive to be matched by a
    // later release that began on empty bar.
    [Fact]
    public void A_Cancelled_Press_Does_Not_Launch_From_A_Later_Press_That_Began_Elsewhere()
    {
        PressOn(_chrome);
        Assert.False(ReleaseOn(null));

        PressOn(null);
        Assert.False(ReleaseOn(_chrome));
    }

    // A press outside the bar never reaches it, so only the release proves the old press is over.
    [Fact]
    public void A_Release_Ends_The_Press_Even_When_The_Next_Press_Is_Not_Seen()
    {
        PressOn(_chrome);
        Assert.False(ReleaseOn(null));

        Assert.False(ReleaseOn(_chrome));
    }

    // And the other way round: a release the bar never saw (lost to another window) is ended by
    // the next press, which is a fresh press on empty bar.
    [Fact]
    public void A_Press_On_Empty_Bar_Ends_A_Press_Whose_Release_Was_Not_Seen()
    {
        PressOn(_chrome);

        PressOn(null);
        Assert.False(ReleaseOn(_chrome));
    }

    // The window's tunnelling press handler must not undo the icon's: it runs first.
    [Fact]
    public void A_Press_On_Empty_Bar_Then_On_An_Icon_Is_Not_A_Press_On_Empty_Bar()
    {
        PressOn(null);
        PressOn(_chrome);
        Assert.True(ReleaseOn(_chrome));
    }

    [Fact]
    public void Leaving_The_Bar_Cancels()
    {
        PressOn(_chrome);
        _tracker.Cancel();
        Assert.False(ReleaseOn(_chrome));
    }
}
