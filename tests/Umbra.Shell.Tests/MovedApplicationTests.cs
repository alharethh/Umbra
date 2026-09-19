using System.IO;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// An application that has updated into a new directory, and so changed identity.
/// </summary>
/// <remarks>
/// <para>
/// A path-keyed application carries its version in its identity. Discord went from
/// <c>Discord\app-1.0.9253\Discord.exe</c> to <c>Discord\app-1.0.9258\Discord.exe</c>, so the
/// running application stopped matching the pin and <b>the bar drew it twice</b>: a pinned icon
/// that could only launch it, and a running icon that could not be pinned. Neither could close it,
/// because the pinned one believed nothing was running.
/// </para>
/// <para>
/// Healing the pin's <i>path</i> is not enough and cannot even happen: that runs only when a pin is
/// seen running, which is exactly what has stopped. The key has to move too, which is what breaks
/// the circle.
/// </para>
/// </remarks>
public class MovedApplicationTests : IDisposable
{
    private const string Old = @"C:\Users\T\AppData\Local\Discord\app-1.0.9253\Discord.exe";
    private const string New = @"C:\Users\T\AppData\Local\Discord\app-1.0.9258\Discord.exe";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"umbra-moved-{Guid.NewGuid():N}.txt");

    private PinSettings Seeded(params (string Key, string Path, string Name)[] pins)
    {
        File.WriteAllLines(_path,
            ["# Pinned applications, in bar order. key|path|name",
             .. pins.Select(p => $"{p.Key}|{p.Path}|{p.Name}")]);

        return new PinSettings(_path);
    }

    [Fact]
    public void AVersionedDirectoryChangeIsRecognised()
    {
        Assert.True(AppIdentity.IsSameApplicationMoved(Old, New));
    }

    /// <summary>
    /// The rule is narrow on purpose.
    /// </summary>
    /// <remarks>
    /// It decides that two different executables are the same application, so it has to be hard to
    /// satisfy by accident: the same file name, in sibling directories, under a shared parent.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Apps\Discord\v1\Discord.exe", @"C:\Apps\Discord\v2\Update.exe")]      // different file
    [InlineData(@"C:\Apps\Discord\v1\Discord.exe", @"D:\Other\Thing\v2\Discord.exe")]      // unrelated tree
    [InlineData(@"C:\Apps\A\v1\app.exe", @"C:\Apps\B\v2\app.exe")]                         // different applications
    [InlineData(@"C:\Apps\Discord\v1\Discord.exe", @"C:\Apps\Discord\v1\Discord.exe")]     // not moved at all
    public void UnrelatedExecutablesAreNotAdopted(string dead, string live)
    {
        Assert.False(AppIdentity.IsSameApplicationMoved(dead, live));
    }

    [Fact]
    public void AnAppUserModelIdIsNeverTreatedAsMoved()
    {
        // Two of those are either equal or unrelated; there is no "moved" for them.
        Assert.False(AppIdentity.IsSameApplicationMoved("308046B0AF4A39CB", "308046B0AF4A39CC"));
        Assert.False(AppIdentity.IsSameApplicationMoved(null, New));
        Assert.False(AppIdentity.IsSameApplicationMoved(Old, "   "));
    }

    [Fact]
    public void RekeyMovesBothTheKeyAndThePath()
    {
        PinSettings pins = Seeded((Old, Old, "Discord"));

        Assert.True(pins.Rekey(Old, New));
        Assert.Equal(New, pins.Pins[0].Key);
        Assert.Equal(New, pins.Pins[0].Path);
        Assert.Equal("Discord", pins.Pins[0].Name);
    }

    [Fact]
    public void TheNewIdentitySurvivesARestart()
    {
        PinSettings pins = Seeded((Old, Old, "Discord"));
        pins.Rekey(Old, New);

        Assert.Equal(New, new PinSettings(_path).Pins[0].Key);
    }

    /// <summary>
    /// Adopting an identity that is already pinned removes the stale entry rather than duplicating.
    /// </summary>
    /// <remarks>
    /// Reachable when the user pinned the application again under its new identity before the
    /// adoption ran. Two pins for one application would then show as two icons for good.
    /// </remarks>
    [Fact]
    public void AdoptingAnIdentityThatIsAlreadyPinnedDropsTheStaleOne()
    {
        PinSettings pins = Seeded((Old, Old, "Discord"), (New, New, "Discord"));

        Assert.True(pins.Rekey(Old, New));
        Assert.Single(pins.Pins);
        Assert.Equal(New, pins.Pins[0].Key);
    }

    /// <summary>
    /// The row keeps the position the user chose.
    /// </summary>
    /// <remarks>
    /// Letting the reconcile drop the old key and append the new one would work, and would send a
    /// deliberately-placed pinned icon to the end of the row — once, mysteriously, on whichever
    /// launch happened to follow an application update.
    /// </remarks>
    [Fact]
    public void TheRowKeepsThePinnedPositionWhenAnApplicationMoves()
    {
        BarOrder order = new();
        order.Reconcile([Old], ["a", "b"]);
        order.Move(Old, 1);

        Assert.Equal([.. (string[])["a", Old, "b"]], order.Keys);

        Assert.True(order.Rename(Old, New));
        Assert.Equal([.. (string[])["a", New, "b"]], order.Keys);
    }

    /// <summary>Where both identities are already on the row, the pinned position wins.</summary>
    [Fact]
    public void RenamingOntoAnIdentityAlreadyOnTheRowKeepsTheOldPosition()
    {
        BarOrder order = new();
        order.Reconcile([Old], [New, "b"]);

        // Pinned-but-not-running Old first, then the running New, then b.
        Assert.Equal([.. (string[])[Old, New, "b"]], order.Keys);

        Assert.True(order.Rename(Old, New));
        Assert.Equal([.. (string[])[New, "b"]], order.Keys);
    }

    [Fact]
    public void RenamingSomethingAbsentChangesNothing()
    {
        BarOrder order = new();
        order.Reconcile([], ["a", "b"]);

        Assert.False(order.Rename("missing", "other"));
        Assert.False(order.Rename("a", "a"));
        Assert.Equal([.. (string[])["a", "b"]], order.Keys);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }
}
