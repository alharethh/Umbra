using System.Windows;

namespace Umbra.Shell.Rendering;

/// <summary>
/// The geometry of dragging a notification icon between the overflow and the bar, kept free of
/// windows so it can be tested.
/// </summary>
internal static class TrayDrop
{
    /// <summary>Where a dragged icon would land.</summary>
    public enum Kind
    {
        /// <summary>Off the bar and off the menu: the drag is dropped and nothing changes.</summary>
        None,

        /// <summary>On the bar, in a gap between the chosen icons.</summary>
        Bar,

        /// <summary>Back in the overflow menu.</summary>
        Overflow,
    }

    /// <summary>
    /// Whether the pointer has moved far enough from the press to be a drag rather than a click.
    /// Either axis alone is enough, as for every Windows drag.
    /// </summary>
    public static bool IsDrag(Point start, Point now, double thresholdX, double thresholdY) =>
        Math.Abs(now.X - start.X) >= thresholdX || Math.Abs(now.Y - start.Y) >= thresholdY;

    /// <summary>
    /// The gap a point along the bar falls into: the index of the first slot whose middle lies
    /// beyond it, or the slot count for the end of the row.
    /// </summary>
    /// <param name="slots">Each slot's start and end along the bar's long axis, in row order.</param>
    /// <remarks>
    /// The middle rather than the edge, so a drop over the near half of an icon goes in front of it
    /// and over the far half behind it, which is where the caret is drawn.
    /// </remarks>
    public static int GapIndex(IReadOnlyList<(double Start, double End)> slots, double along)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            if (along < (slots[i].Start + slots[i].End) / 2) return i;
        }

        return slots.Count;
    }
}

/// <summary>The chevron's right-click menu.</summary>
internal static class TrayChevronMenu
{
    /// <summary>The one item: the switch the user is not on.</summary>
    public static string ToggleLabel(bool showAll) =>
        showAll ? "Put icons back in the menu" : "Show all icons on the bar";
}
