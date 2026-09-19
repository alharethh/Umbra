using System.IO;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// The pin file: the set of pinned applications, and enough of their order to restore a layout.
/// </summary>
/// <remarks>
/// It no longer decides where anything sits — <see cref="BarOrder"/> does, and
/// <see cref="BarOrderTests"/> covers that. What is tested here is that the file follows the row
/// faithfully and does not lose entries on the way, because a pin file that silently drops a line
/// costs the user a layout they cannot get back.
/// </remarks>
public class PinSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"umbra-pins-{Guid.NewGuid():N}.txt");

    private PinSettings Seeded(params string[] keys)
    {
        File.WriteAllLines(_path,
            ["# Pinned applications, in bar order. key|path|name",
             .. keys.Select(k => $"{k}|C:\\{k}.exe|{k}")]);

        return new PinSettings(_path);
    }

    private static string[] Order(PinSettings pins) => [.. pins.Pins.Select(p => p.Key)];

    [Fact]
    public void Load_Preserves_File_Order()
    {
        PinSettings pins = Seeded("a", "b", "c");
        Assert.Equal(["a", "b", "c"], Order(pins));
    }

    [Fact]
    public void Reorder_Follows_The_Row()
    {
        PinSettings pins = Seeded("a", "b", "c");

        Assert.True(pins.Reorder(["b", "c", "a"]));
        Assert.Equal(["b", "c", "a"], Order(pins));
    }

    [Fact]
    public void Reordering_To_The_Order_It_Already_Has_Changes_Nothing()
    {
        // The model calls this on every window event and writes only when it reports a change, so
        // a false positive here puts a file write on every window that opens anywhere on the
        // desktop.
        PinSettings pins = Seeded("a", "b", "c");

        Assert.False(pins.Reorder(["a", "b", "c"]));
        Assert.Equal(["a", "b", "c"], Order(pins));
    }

    /// <summary>
    /// The row names pins interleaved with running applications, and hands back only the pins.
    /// </summary>
    /// <remarks>
    /// A pin dragged past a running icon has nowhere to say so in a file that can only hold pins.
    /// What survives is the relative order, which is the part that can be restored.
    /// </remarks>
    [Fact]
    public void Only_The_Relative_Order_Of_Pins_Survives()
    {
        PinSettings pins = Seeded("a", "b", "c");

        Assert.True(pins.Reorder(["c", "a", "b"]));
        Assert.Equal(["c", "a", "b"], Order(pins));
    }

    /// <summary>
    /// A pin the row has not named must not be dropped.
    /// </summary>
    /// <remarks>
    /// This is the failure worth guarding: the obvious implementation builds a new list from the
    /// keys it was given, and any pin missing from that list — one the row has not caught up with,
    /// or one whose key spelling differs — silently vanishes from the user's layout on the next
    /// write. Nothing about the bar would look wrong until the next launch.
    /// </remarks>
    [Fact]
    public void A_Pin_The_Row_Did_Not_Name_Keeps_Its_Place()
    {
        PinSettings pins = Seeded("a", "b", "c");

        pins.Reorder(["c", "a"]);
        Assert.Equal(["c", "a", "b"], Order(pins));
    }

    [Fact]
    public void Reorder_Ignores_Keys_That_Are_Not_Pinned()
    {
        // The row carries running applications too, and hands back whatever passed its filter.
        PinSettings pins = Seeded("a", "b");

        Assert.True(pins.Reorder(["missing", "b", "a"]));
        Assert.Equal(["b", "a"], Order(pins));
    }

    [Fact]
    public void Keys_Are_Matched_The_Way_Windows_Matches_Paths()
    {
        PinSettings pins = Seeded("C:\\App.exe", "C:\\Other.exe");

        Assert.True(pins.Reorder(["c:\\other.exe", "c:\\app.exe"]));
        Assert.Equal(["C:\\Other.exe", "C:\\App.exe"], Order(pins));
    }

    /// <summary>
    /// A reorder is in memory; the file changes when — and only when — the drag ends.
    /// </summary>
    /// <remarks>
    /// A drag crosses every position between where it started and where it ends. Writing each of
    /// those puts a file write on a mouse-move handler and records orders the user never chose.
    /// </remarks>
    [Fact]
    public void Reorder_Does_Not_Write_Until_Persist_Is_Called()
    {
        PinSettings pins = Seeded("a", "b", "c");

        Assert.True(pins.Reorder(["b", "c", "a"]));
        Assert.Equal(["b", "c", "a"], Order(pins));

        // In memory only: a fresh read of the same file still sees the original order.
        Assert.Equal(["a", "b", "c"], Order(new PinSettings(_path)));

        pins.Persist();
        Assert.Equal(["b", "c", "a"], Order(new PinSettings(_path)));
    }

    /// <summary>
    /// <c>Persist</c> does not raise <c>Changed</c>.
    /// </summary>
    /// <remarks>
    /// The caller is the model, which has just finished arranging the row. Telling it to rebuild
    /// what it has already built would be a round trip for nothing, and during a drag it would run
    /// on every drop.
    /// </remarks>
    [Fact]
    public void Persist_Does_Not_Ask_The_Bar_To_Rebuild()
    {
        PinSettings pins = Seeded("a", "b", "c");

        int raised = 0;
        pins.Changed += () => raised++;

        pins.Reorder(["c", "b", "a"]);
        pins.Persist();

        Assert.Equal(0, raised);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }
}
