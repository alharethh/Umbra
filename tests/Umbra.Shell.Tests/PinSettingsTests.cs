using System.IO;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// The pin order, which is what drag-reorder writes and what the bar's slot order is built from.
/// </summary>
/// <remarks>
/// The drag interaction itself needs a pointer and a live visual tree, so it is verified by running
/// the bar. What is testable — and what silently breaks the user's layout if it is wrong — is the
/// ordering and the persistence underneath it.
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
    public void Move_Reorders_And_Reports_The_Change()
    {
        PinSettings pins = Seeded("a", "b", "c");

        Assert.True(pins.Move("a", 2));
        Assert.Equal(["b", "c", "a"], Order(pins));
    }

    [Fact]
    public void Move_Backwards_Works_Too()
    {
        PinSettings pins = Seeded("a", "b", "c");

        Assert.True(pins.Move("c", 0));
        Assert.Equal(["c", "a", "b"], Order(pins));
    }

    [Fact]
    public void Move_To_The_Same_Index_Changes_Nothing()
    {
        PinSettings pins = Seeded("a", "b", "c");

        Assert.False(pins.Move("b", 1));
        Assert.Equal(["a", "b", "c"], Order(pins));
    }

    [Fact]
    public void Move_Clamps_Rather_Than_Throwing()
    {
        // A drag can run off either end of the row, and the pointer is not obliged to stay inside.
        PinSettings pins = Seeded("a", "b", "c");

        pins.Move("a", 99);
        Assert.Equal(["b", "c", "a"], Order(pins));

        pins.Move("a", -99);
        Assert.Equal(["a", "b", "c"], Order(pins));
    }

    [Fact]
    public void Moving_An_Unpinned_Key_Is_Ignored()
    {
        PinSettings pins = Seeded("a", "b");

        Assert.False(pins.Move("missing", 0));
        Assert.Equal(["a", "b"], Order(pins));
    }

    [Fact]
    public void A_Persisted_Move_Survives_A_Reload()
    {
        PinSettings pins = Seeded("a", "b", "c");
        pins.Move("a", 2);

        Assert.Equal(["b", "c", "a"], Order(new PinSettings(_path)));
    }

    /// <summary>
    /// The drag path reorders without writing, then persists once on drop.
    /// </summary>
    /// <remarks>
    /// A drag crosses every position between where it started and where it ends. Writing each of
    /// those puts a file write on a mouse-move handler and records orders the user never chose — so
    /// the intermediate moves must reorder in memory only, and the file must still say what it said
    /// before the drop.
    /// </remarks>
    [Fact]
    public void An_Unpersisted_Move_Is_Not_Written_Until_Persist_Is_Called()
    {
        PinSettings pins = Seeded("a", "b", "c");

        Assert.True(pins.Move("a", 2, persist: false));
        Assert.Equal(["b", "c", "a"], Order(pins));

        // In memory only: a fresh read of the same file still sees the original order.
        Assert.Equal(["a", "b", "c"], Order(new PinSettings(_path)));

        pins.Persist();
        Assert.Equal(["b", "c", "a"], Order(new PinSettings(_path)));
    }

    [Fact]
    public void Both_Move_Forms_Raise_Changed_So_The_Bar_Follows_The_Pointer()
    {
        PinSettings pins = Seeded("a", "b", "c");

        int raised = 0;
        pins.Changed += () => raised++;

        pins.Move("a", 2, persist: false);
        Assert.Equal(1, raised);

        pins.Move("a", 0, persist: true);
        Assert.Equal(2, raised);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }
}
