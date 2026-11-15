using Umbra.Shell.Rendering;

namespace Umbra.Shell.Tests;

/// <summary>
/// The bar's row making room for a dragged notification icon: where each icon and the chevron
/// are drawn, and which gap the pointer is over.
/// </summary>
/// <remarks>
/// The rule these pin is the owner's: <b>nothing the user is aiming at moves while they aim.</b>
/// An icon dragged off the bar toward the chevron must find the chevron where it was.
/// </remarks>
public class TrayRoomTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void From_The_Bar_The_Chevron_Never_Moves(int count)
    {
        for (int from = 0; from < count; from++)
        {
            Assert.Equal(0, TrayDrop.Room(count, from, gap: null).ChevronOffset);
            for (int gap = 0; gap < count; gap++)
                Assert.Equal(0, TrayDrop.Room(count, from, gap).ChevronOffset);
        }
    }

    [Fact]
    public void Off_The_Row_The_Icons_Old_Slot_Stays_Open_And_Nothing_Moves()
    {
        TrayDrop.RoomLayout room = TrayDrop.Room(4, from: 1, gap: null);

        Assert.All(room.Offsets, offset => Assert.Equal(0, offset));
        Assert.Equal(1, room.GapSlot);
    }

    [Theory]
    [InlineData(4, 3, 0, new[] { 1, 1, 1, 0 }, 0)]
    [InlineData(4, 0, 3, new[] { 0, -1, -1, -1 }, 3)]
    [InlineData(4, 1, 2, new[] { 0, 0, -1, 0 }, 2)]
    [InlineData(4, 2, 2, new[] { 0, 0, 0, 0 }, 2)]
    public void Along_The_Bar_The_Gap_Travels_With_The_Pointer(int count, int from, int gap, int[] offsets, int slot)
    {
        TrayDrop.RoomLayout room = TrayDrop.Room(count, from, gap);

        Assert.Equal(offsets, room.Offsets);
        Assert.Equal(slot, room.GapSlot);
    }

    [Theory]
    [InlineData(3, 0, new[] { 0, 0, 0 }, -1)]
    [InlineData(3, 1, new[] { -1, 0, 0 }, 0)]
    [InlineData(3, 3, new[] { -1, -1, -1 }, 2)]
    [InlineData(0, 0, new int[0], -1)]
    public void From_The_Menu_The_Row_Grows_Toward_The_Chevron_Which_Moves_Out_One(int count, int gap, int[] offsets, int slot)
    {
        TrayDrop.RoomLayout room = TrayDrop.Room(count, from: null, gap);

        Assert.Equal(offsets, room.Offsets);
        Assert.Equal(-1, room.ChevronOffset);
        Assert.Equal(slot, room.GapSlot);
    }

    [Fact]
    public void From_The_Menu_Off_The_Row_Nothing_Moves_And_Nothing_Is_Open()
    {
        TrayDrop.RoomLayout room = TrayDrop.Room(3, from: null, gap: null);

        Assert.All(room.Offsets, offset => Assert.Equal(0, offset));
        Assert.Equal(0, room.ChevronOffset);
        Assert.Null(room.GapSlot);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void Every_Slot_Is_Drawn_Exactly_Once(int count)
    {
        // From the bar: the other icons and the gap fill the row's own slots, one each.
        for (int from = 0; from < count; from++)
            for (int gap = 0; gap < count; gap++)
            {
                TrayDrop.RoomLayout room = TrayDrop.Room(count, from, gap);
                List<int> drawn = [.. Enumerable.Range(0, count).Where(j => j != from).Select(j => j + room.Offsets[j]), room.GapSlot!.Value];
                Assert.Equal(Enumerable.Range(0, count), drawn.Order());
            }

        // From the menu: one slot more, starting one before the first.
        for (int gap = 0; gap <= count; gap++)
        {
            TrayDrop.RoomLayout room = TrayDrop.Room(count, null, gap);
            List<int> drawn = [.. Enumerable.Range(0, count).Select(j => j + room.Offsets[j]), room.GapSlot!.Value];
            Assert.Equal(Enumerable.Range(-1, count + 1), drawn.Order());
        }
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    public void From_The_Bar_The_Gap_Under_The_Pointer_Is_The_One_That_Opens(int count, int slot)
    {
        // Slots 20 long from 100. Anywhere in drawn slot s, the gap is s, and Room draws the gap in s.
        foreach (double inside in new[] { 0.1, 10, 19.9 })
        {
            int gap = TrayDrop.RoomGapIndex(count, fromBar: true, firstSlotStart: 100, pitch: 20, along: 100 + slot * 20 + inside);
            Assert.Equal(slot, gap);
            Assert.Equal(slot, TrayDrop.Room(count, from: 1, gap).GapSlot);
        }
    }

    [Theory]
    [InlineData(3, -1)]
    [InlineData(3, 0)]
    [InlineData(3, 2)]
    public void From_The_Menu_The_Gap_Under_The_Pointer_Is_The_One_That_Opens(int count, int slot)
    {
        foreach (double inside in new[] { 0.1, 10, 19.9 })
        {
            int gap = TrayDrop.RoomGapIndex(count, fromBar: false, firstSlotStart: 100, pitch: 20, along: 100 + slot * 20 + inside);
            Assert.Equal(slot, TrayDrop.Room(count, from: null, gap).GapSlot);
        }
    }

    [Fact]
    public void Beyond_Either_End_The_Gap_Is_The_End()
    {
        Assert.Equal(0, TrayDrop.RoomGapIndex(4, true, 100, 20, -500));
        Assert.Equal(3, TrayDrop.RoomGapIndex(4, true, 100, 20, 5000));
        Assert.Equal(4, TrayDrop.RoomGapIndex(4, false, 100, 20, 5000));
        Assert.Equal(0, TrayDrop.RoomGapIndex(0, false, 100, 20, 140));
        Assert.Equal(0, TrayDrop.RoomGapIndex(1, true, 100, 20, 140));
    }
}
