using System.Windows.Media;
using Umbra.Shell.Rendering;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// The network glyphs, which are drawn by us because Windows never sends the icon.
/// </summary>
/// <remarks>
/// Path data is parsed from strings, and a typo in one throws inside a static initialiser — which
/// in a WPF application surfaces as a <c>TypeInitializationException</c> on the first render of the
/// bar, not as a build error. These parse them all, so a bad path fails here instead.
/// </remarks>
public class NetworkGlyphTests
{
    // Taken as an int and cast, because NetworkKind is internal and xUnit needs a public method:
    // a public signature cannot name an internal type even where InternalsVisibleTo applies.
    [Theory]
    [InlineData((int)NetworkKind.WiFi)]
    [InlineData((int)NetworkKind.Ethernet)]
    [InlineData((int)NetworkKind.Offline)]
    public void Every_Glyph_Parses_And_Has_Area(int value)
    {
        Geometry glyph = NetworkGlyphs.For((NetworkKind)value);

        Assert.False(glyph.Bounds.IsEmpty);
        Assert.InRange(glyph.Bounds.Width, 1.0, 14.0);
        Assert.InRange(glyph.Bounds.Height, 1.0, 14.0);
    }

    /// <summary>
    /// Frozen, because they are built once and read on every render.
    /// </summary>
    [Fact]
    public void The_Glyphs_Are_Frozen() =>
        Assert.All(
            (NetworkKind[])[NetworkKind.WiFi, NetworkKind.Ethernet, NetworkKind.Offline],
            kind => Assert.True(NetworkGlyphs.For(kind).IsFrozen));

    /// <summary>
    /// Three connection states, three different drawings.
    /// </summary>
    /// <remarks>
    /// The failure this catches is a copy-paste in the lookup — two states mapped to one glyph.
    /// It is invisible in review and, on a machine that is always on Ethernet, invisible in use
    /// too, right up until someone unplugs the cable.
    /// </remarks>
    [Fact]
    public void Each_Connection_Kind_Draws_Something_Different()
    {
        string wifi = NetworkGlyphs.For(NetworkKind.WiFi).ToString();
        string ethernet = NetworkGlyphs.For(NetworkKind.Ethernet).ToString();
        string offline = NetworkGlyphs.For(NetworkKind.Offline).ToString();

        Assert.NotEqual(wifi, ethernet);
        Assert.NotEqual(wifi, offline);
        Assert.NotEqual(ethernet, offline);
    }

    /// <summary>An unknown state shows the disconnected glyph, never nothing.</summary>
    [Fact]
    public void An_Unknown_Kind_Falls_Back_To_Offline() =>
        Assert.Equal(
            NetworkGlyphs.For(NetworkKind.Offline).ToString(),
            NetworkGlyphs.For((NetworkKind)99).ToString());
}
