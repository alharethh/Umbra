using System.Windows;

namespace Umbra.Shell.Rendering;

/// <summary>
/// What the away blackout does to the bar's content, apart from the fade itself.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>BarWindow</c> only so a test can drive it against a real window: the bar
/// itself cannot be built in one.
/// </para>
/// <para>
/// <b>Out of hit-testing from the first frame of the fade</b>, because a stock tooltip needs
/// nothing but a pointer resting on its owner. Closing the open tooltips at the start of the fade
/// was enough when the user walked away from a running bar, and not when the bar started with
/// them already away: the fade then begins before the first paint, the window appears under a
/// pointer that has not moved, and the tooltip it raises a second later stayed lit over the black
/// strip (backlog F5c). Hidden would stop it too, but the content still has to be drawn for a
/// second and a half of fading.
/// </para>
/// <para>
/// <b>And the tooltips closed again once it is black</b>, because a tooltip outlives its owner
/// going hidden: WPF does not close it, and one opened before the hit-test change reached the
/// pointer would otherwise stay up.
/// </para>
/// </remarks>
internal static class AwayContent
{
    /// <summary>The fade has started: nothing in <paramref name="content"/> may be hovered.</summary>
    public static void Darken(UIElement content) => content.IsHitTestVisible = false;

    /// <summary>The fade has finished: the content is hidden and any tooltip still up is closed.</summary>
    /// <returns>How many tooltips were closed, for the log.</returns>
    public static int Hide(UIElement content)
    {
        content.Visibility = Visibility.Hidden;
        return OpenToolTips.CloseAll();
    }

    /// <summary>The user is back.</summary>
    public static void Restore(UIElement content)
    {
        content.Visibility = Visibility.Visible;
        content.IsHitTestVisible = true;
    }
}
