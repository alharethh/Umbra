using System.IO;
using Umbra.Shell.JumpList;

namespace Umbra.Shell.Tests;

/// <summary>
/// Reading an application's published jump list tasks.
/// </summary>
/// <remarks>
/// The link parsing itself is Windows' — <c>IPersistStream::Load</c> on a real <c>ShellLink</c> —
/// and is not worth testing, because testing it would only test the shell. What is ours, and what
/// silently produces an empty menu when it is wrong, is finding the links inside the container and
/// deciding which file belongs to which application.
/// </remarks>
public class JumpListTests
{
    /// <summary>A shell link header: size 0x4C then the link CLSID.</summary>
    private static readonly byte[] Header =
    [
        0x4C, 0x00, 0x00, 0x00,
        0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46,
    ];

    private static byte[] Container(params int[] gaps)
    {
        List<byte> bytes = [];

        foreach (int gap in gaps)
        {
            bytes.AddRange(Enumerable.Repeat((byte)0xAB, gap));
            bytes.AddRange(Header);
        }

        return [.. bytes];
    }

    [Fact]
    public void FindsEveryLinkInOrder()
    {
        List<int> found = ShellLinkReader.FindLinks(Container(4, 30, 12));

        Assert.Equal(3, found.Count);
        Assert.Equal(4, found[0]);
        Assert.Equal(4 + 20 + 30, found[1]);
        Assert.Equal(4 + 20 + 30 + 20 + 12, found[2]);
    }

    [Fact]
    public void FindsNothingInAFileWithNoLinks()
    {
        Assert.Empty(ShellLinkReader.FindLinks([.. Enumerable.Repeat((byte)0x00, 4096)]));
        Assert.Empty(ShellLinkReader.FindLinks([]));
    }

    /// <summary>A prefix of the signature is not a link.</summary>
    /// <remarks>
    /// The first four bytes are a plain length field and turn up constantly in binary data; it is
    /// the CLSID after them that makes the match meaningful. Stopping the comparison early would
    /// hand arbitrary bytes to the shell and log a failure for every one of them.
    /// </remarks>
    [Fact]
    public void APartialSignatureIsNotAMatch()
    {
        byte[] bytes = [.. Header[..12], .. Enumerable.Repeat((byte)0xFF, 40)];

        Assert.Empty(ShellLinkReader.FindLinks(bytes));
    }

    [Fact]
    public void StopsAtTheLimit()
    {
        // The index only needs the first link of each file: reading all of them would multiply its
        // cost by the length of every jump list on the machine.
        Assert.Single(ShellLinkReader.FindLinks(Container(0, 8, 8), limit: 1));
        Assert.Equal(2, ShellLinkReader.FindLinks(Container(0, 8, 8), limit: 2).Count);
    }

    /// <summary>A link butted directly against the previous one is still found.</summary>
    /// <remarks>
    /// The scan skips its own matched header before continuing. Skipping one byte too many would
    /// lose a link that starts immediately after another, which is exactly how these files are
    /// packed.
    /// </remarks>
    [Fact]
    public void AdjacentLinksAreBothFound()
    {
        List<int> found = ShellLinkReader.FindLinks(Container(0, 0, 0));

        Assert.Equal(3, found.Count);
        Assert.Equal([0, 20, 40], found);
    }

    [Fact]
    public void AnApplicationIsKnownByItsPathAndItsFileName()
    {
        string[] keys = JumpListIndex.Candidates("308046B0AF4A39CB", @"C:\Program Files\Mozilla Firefox\firefox.exe");

        Assert.Contains("308046B0AF4A39CB", keys);
        Assert.Contains(@"C:\Program Files\Mozilla Firefox\firefox.exe", keys);
        Assert.Contains("firefox.exe", keys);
    }

    /// <summary>
    /// The bare file name is what survives an application updating.
    /// </summary>
    /// <remarks>
    /// Packaged applications install into a versioned directory and move on every release — the
    /// development machine's Notes app went from <c>Contoso.Notes_1.32352.1.0_x64__…</c> to
    /// <c>Contoso.Notes_2.2553.1.0_x64__…</c> mid-project. Matching on the full path alone would lose the
    /// jump list at the next update, silently and permanently.
    /// </remarks>
    [Fact]
    public void TheFileNameSurvivesAVersionedPathChanging()
    {
        string[] before = JumpListIndex.Candidates(null, @"C:\Program Files\WindowsApps\Contoso.Notes_1.32352.1.0_x64__p\app\notes.exe");
        string[] after = JumpListIndex.Candidates(null, @"C:\Program Files\WindowsApps\Contoso.Notes_2.2553.1.0_x64__p\app\notes.exe");

        Assert.Contains("notes.exe", before);
        Assert.Contains("notes.exe", after);
        Assert.DoesNotContain(before[0], after.Where(k => k.Contains('\\')));
    }

    /// <summary>
    /// A UWP application's "path" inside a link is its AUMID behind a nonsense directory.
    /// </summary>
    /// <remarks>
    /// Measured: Edge's first link reports
    /// <c>C:\Users\…\Desktop\Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge</c>. Taking the
    /// file name strips the invented prefix and leaves exactly the identity the bar already knows
    /// the application by.
    /// </remarks>
    [Fact]
    public void AUwpApplicationIsMatchedByTheIdentityBehindItsFakePath()
    {
        string[] keys = JumpListIndex.Candidates(
            null, @"C:\Users\User\Desktop\Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge");

        Assert.Contains("Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge", keys);
    }

    [Fact]
    public void NothingKnownMeansNoCandidates()
    {
        Assert.Empty(JumpListIndex.Candidates(null, null));
        Assert.Empty(JumpListIndex.Candidates("", "   "));
    }

    [Fact]
    public void AnApplicationWithNoJumpListGetsNoTasks()
    {
        // A directory that does not exist stands in for the ordinary case: most applications never
        // call ICustomDestinationList and have no file at all.
        JumpListIndex index = new(Path.Combine(Path.GetTempPath(), $"umbra-no-jumplists-{Guid.NewGuid():N}"));

        Assert.Empty(index.TasksFor("whatever.exe", @"C:\whatever.exe"));
    }

    [Fact]
    public void AnUnusableTaskIsNotOfferedToTheUser()
    {
        // Both occur in real files: the shell filters them out before drawing, and so must we.
        Assert.False(new JumpListTask("", @"C:\app.exe", "", "", 0).IsUsable);
        Assert.False(new JumpListTask("New window", "", "", "", 0).IsUsable);
        Assert.False(new JumpListTask("   ", @"C:\app.exe", "", "", 0).IsUsable);

        Assert.True(new JumpListTask("New window", @"C:\app.exe", "--new", "", 0).IsUsable);
    }
}
