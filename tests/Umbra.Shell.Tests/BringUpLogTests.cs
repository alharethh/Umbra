using System.IO;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Tests;

/// <summary>
/// The log must never be the reason the bar dies.
/// </summary>
/// <remarks>
/// <para>
/// Found 2026-10-24: walking the bar's UI Automation tree with Windows Terminal open made
/// ManagedShell log a warning carrying Terminal's title, which it had cut in the middle of an
/// emoji's surrogate pair. <c>File.AppendAllText</c>'s UTF-8 encoder throws
/// <see cref="System.Text.EncoderFallbackException"/> on a lone surrogate, only I/O and access
/// errors were caught, and the bar exited from the dispatcher. Narrator or any other UI
/// Automation client could have set it off.
/// </para>
/// <para>
/// These write to the real log, which the test suite already shares, so each line carries a
/// marker of its own to be found by.
/// </para>
/// </remarks>
public class BringUpLogTests
{
    [Fact]
    public void A_Lone_Surrogate_Is_Written_Rather_Than_Thrown()
    {
        string marker = $"lone-surrogate-test {Guid.NewGuid():N}";

        BringUpLog.Write($"{marker} title='Terminal \uD83D' tail='\uDE2F'");

        string line = Assert.Single(LinesContaining(marker));
        Assert.Contains("title='Terminal �'", line);
        Assert.Contains("tail='�'", line);
    }

    [Fact]
    public void An_Exception_Message_With_A_Lone_Surrogate_Is_Written_Too()
    {
        string marker = $"lone-surrogate-exception-test {Guid.NewGuid():N}";

        BringUpLog.Write(new InvalidOperationException("bad \uDE2F title"), marker);

        Assert.Single(LinesContaining(marker));
    }

    /// <summary>Read while other tests may be appending, so share the file both ways.</summary>
    private static IEnumerable<string> LinesContaining(string marker)
    {
        using FileStream stream = new(BringUpLog.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);

        return reader.ReadToEnd()
            .Split('\n')
            .Where(l => l.Contains(marker, StringComparison.Ordinal))
            .ToList();
    }
}
