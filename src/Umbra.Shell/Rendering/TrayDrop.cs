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
    /// How the bar's row looks while an icon is dragged: where each icon is drawn, where the
    /// chevron is drawn, and where the open gap is, all in whole slots relative to the row's
    /// laid-out positions.
    /// </summary>
    /// <param name="Offsets">Per icon on the bar, in row order: how many slots it is drawn from its own.</param>
    /// <param name="ChevronOffset">How many slots the chevron is drawn from its own.</param>
    /// <param name="GapSlot">The open slot, in the row's slot numbering (-1 is the slot before the
    /// first icon), or null when nothing is open.</param>
    public sealed record RoomLayout(IReadOnlyList<int> Offsets, int ChevronOffset, int? GapSlot);

    /// <summary>
    /// The row with room made for a dragged icon.
    /// </summary>
    /// <param name="count">Icons on the bar, the dragged one included when it came from there.</param>
    /// <param name="from">The dragged icon's place in the row, or null when it came from the menu.</param>
    /// <param name="gap">Where it would land among the other icons, or null when the pointer is off the row.</param>
    /// <remarks>
    /// <para>
    /// <b>Nothing the user is aiming at may move while they aim at it.</b> The row is pinned to the
    /// clock's side, so a slot appearing or disappearing shifts everything on the far side of it,
    /// the chevron included. So:
    /// </para>
    /// <list type="bullet">
    /// <item>An icon dragged off the bar keeps its slot open until it is dropped, so the row keeps
    /// its length and the chevron stays where it is while the user carries the icon to it.</item>
    /// <item>Along the bar the gap travels with the pointer: the old slot closes as the new one
    /// opens, so the row still keeps its length.</item>
    /// <item>From the menu, the row grows by a slot where the icon would land, and the chevron moves
    /// out by one. That is the one time it moves, and the user is aiming at the row then.</item>
    /// </list>
    /// </remarks>
    public static RoomLayout Room(int count, int? from, int? gap)
    {
        int[] offsets = new int[count];

        if (from is int origin)
        {
            // Off the row: its own slot stays open, and nothing moves.
            if (gap is not int target) return new RoomLayout(offsets, 0, origin);

            for (int j = 0; j < count; j++)
            {
                if (j == origin) continue;
                int among = j < origin ? j : j - 1;
                int drawn = among < target ? among : among + 1;
                offsets[j] = drawn - j;
            }

            return new RoomLayout(offsets, 0, target);
        }

        if (gap is not int landing) return new RoomLayout(offsets, 0, null);

        // From the menu: one slot more, opening toward the chevron, which moves out by one.
        for (int j = 0; j < count; j++)
            offsets[j] = (j < landing ? j : j + 1) - j - 1;

        return new RoomLayout(offsets, -1, landing - 1);
    }

    /// <summary>
    /// Which gap the pointer is over, measured against the row as <see cref="Room"/> draws it, so
    /// the gap under the pointer is the one that opens and it does not flicker as the row moves.
    /// </summary>
    /// <param name="count">Icons on the bar, the dragged one included when it came from there.</param>
    /// <param name="fromBar">Whether the dragged icon came from the bar.</param>
    /// <param name="firstSlotStart">Where the row's first laid-out slot starts along the bar.</param>
    /// <param name="pitch">One slot's length along the bar.</param>
    /// <param name="along">The pointer along the bar.</param>
    public static int RoomGapIndex(int count, bool fromBar, double firstSlotStart, double pitch, double along)
    {
        // From the bar the drawn row is the laid-out one; from the menu it starts a slot earlier.
        int others = fromBar ? count - 1 : count;
        double start = fromBar ? firstSlotStart : firstSlotStart - pitch;
        if (pitch <= 0 || others <= 0) return 0;

        return Math.Clamp((int)Math.Floor((along - start) / pitch), 0, others);
    }
}

/// <summary>The chevron's right-click menu.</summary>
internal static class TrayChevronMenu
{
    public enum Item { ShowAll, PutAllBack }

    public const string ShowAllLabel = "Show all icons on the bar";
    public const string PutAllBackLabel = "Put icons back in the menu";

    public static string LabelOf(Item item) => item == Item.ShowAll ? ShowAllLabel : PutAllBackLabel;

    /// <summary>
    /// What the menu offers, decided by where the icons are rather than by the "show all" switch.
    /// </summary>
    /// <param name="showAll">Whether "show all" is on.</param>
    /// <param name="onBar">Icons drawn on the bar, the system indicators left out.</param>
    /// <param name="inMenu">Icons behind the chevron.</param>
    /// <remarks>
    /// Decided by the switch, the menu offered only "Show all icons on the bar" after a drop on the
    /// chevron had ended "show all" with every other icon still on the bar, so there was no way to
    /// put them back from here. "Put icons back" is offered whenever an icon is on the bar, and
    /// while "show all" is on with none yet, so that it can still be left.
    /// </remarks>
    public static IReadOnlyList<Item> Offers(bool showAll, int onBar, int inMenu)
    {
        List<Item> items = [];
        if (inMenu > 0) items.Add(Item.ShowAll);
        if (onBar > 0 || showAll) items.Add(Item.PutAllBack);
        return items;
    }
}
