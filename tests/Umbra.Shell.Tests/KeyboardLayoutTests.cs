using System.Globalization;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// The input language indicator: what it is called, and what clicking it selects next.
/// </summary>
/// <remarks>
/// Only the pure half is testable here — reading the foreground window's layout needs a desktop
/// with a foreground window on it. That is the half that has no judgement in it; the naming and
/// the cycling are where the mistakes live.
/// </remarks>
public class KeyboardLayoutTests
{
    /// <summary>English (UK), which is one of the two installed on the development machine.</summary>
    private const int EnglishUk = 0x0809;

    /// <summary>Norwegian Bokmål — the <c>NOB</c> that prompted the feature.</summary>
    private const int NorwegianBokmal = 0x0414;

    [Theory]
    [InlineData(NorwegianBokmal, "NOB")]
    [InlineData(EnglishUk, "ENG")]
    [InlineData(0x0409, "ENG")]
    [InlineData(0x0407, "DEU")]
    public void The_Indicator_Names_The_Language(int languageId, string expected) =>
        Assert.Equal(expected, KeyboardLayoutWatcher.NameFor(languageId));

    /// <summary>
    /// The three-letter <b>ISO</b> name, not Windows' own three-letter name.
    /// </summary>
    /// <remarks>
    /// They disagree on exactly the language this was asked for. .NET's
    /// <c>ThreeLetterWindowsLanguageName</c> is the obvious property to reach for and gives
    /// <c>NOR</c>; the taskbar shows <c>NOB</c>. English agrees under both, so a test written
    /// against English alone would pass either way — which is why this one is written against
    /// Norwegian.
    /// </remarks>
    [Fact]
    public void Windows_Own_Three_Letter_Name_Would_Be_Wrong()
    {
        CultureInfo norwegian = CultureInfo.GetCultureInfo(NorwegianBokmal);

        Assert.Equal("NOR", norwegian.ThreeLetterWindowsLanguageName);
        Assert.Equal("NOB", KeyboardLayoutWatcher.NameFor(NorwegianBokmal));
    }

    /// <summary>
    /// A layout handle carries the physical layout in its high word, and it is not part of the
    /// name.
    /// </summary>
    /// <remarks>
    /// US English and US-Dvorak are two layouts for one language and the indicator shows one thing
    /// for both, as Windows does. Masking the wrong half gives a language identifier of zero and
    /// a blank indicator for every layout that carries a device in its high word — which is most
    /// of them.
    /// </remarks>
    [Fact]
    public void The_Device_Half_Of_The_Handle_Is_Not_The_Language()
    {
        Assert.Equal("ENG", KeyboardLayoutWatcher.NameFor(unchecked((nint)0xF0FE0409)));
        Assert.Equal("NOB", KeyboardLayoutWatcher.NameFor(0x04140414));
    }

    /// <summary>Nothing to say rather than something wrong.</summary>
    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0666)]
    public void An_Unreadable_Layout_Shows_Nothing(int languageId) =>
        Assert.Equal(string.Empty, KeyboardLayoutWatcher.NameFor(languageId));

    /// <summary>Clicking walks the installed list and wraps.</summary>
    [Fact]
    public void Clicking_Selects_The_Next_Installed_Layout()
    {
        nint[] installed = [NorwegianBokmal, EnglishUk];

        Assert.Equal(EnglishUk, KeyboardLayoutWatcher.NextAfter(NorwegianBokmal, installed));
        Assert.Equal(NorwegianBokmal, KeyboardLayoutWatcher.NextAfter(EnglishUk, installed));
    }

    /// <summary>
    /// A layout that is not in our list still moves the user somewhere.
    /// </summary>
    /// <remarks>
    /// Reachable whenever a language was added since the list was last read — it is re-read every
    /// 30 seconds, not on every tick. Returning nothing would make the click do nothing at all,
    /// for half a minute, with no way for the user to tell why.
    /// </remarks>
    [Fact]
    public void An_Unknown_Current_Layout_Falls_To_The_First()
    {
        nint[] installed = [NorwegianBokmal, EnglishUk];

        Assert.Equal(NorwegianBokmal, KeyboardLayoutWatcher.NextAfter(0x040C, installed));
    }

    [Fact]
    public void With_Nothing_Installed_There_Is_Nothing_To_Select() =>
        Assert.Equal(0, KeyboardLayoutWatcher.NextAfter(EnglishUk, []));

    /// <summary>
    /// The user's languages are read in their order, and their order is numeric.
    /// </summary>
    /// <remarks>
    /// <c>Preload</c>'s value names are numbers written as text, and the registry returns them in
    /// no particular order. Sorted as text, "10" comes before "2" — so a user with ten languages
    /// gets a cycle in an order they never chose, and only that user, which is the kind of thing
    /// that is never reproduced on the machine it was written on.
    /// </remarks>
    [Fact]
    public void The_Preload_Order_Is_Numeric_Not_Alphabetical()
    {
        string[] asTheRegistryGivesThem = ["10", "2", "1", "11", "3"];

        Assert.Equal(
            ["1", "2", "3", "10", "11"],
            KeyboardLayoutWatcher.InPreloadOrder(asTheRegistryGivesThem));
    }

    /// <summary>The key carries more than the numbered list; the rest is not a language.</summary>
    [Fact]
    public void Value_Names_That_Are_Not_Numbers_Are_Not_Languages() =>
        Assert.Equal(
            ["1"],
            KeyboardLayoutWatcher.InPreloadOrder(["1", "(Default)", "Attributes"]));
}
