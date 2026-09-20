using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// The table that names Windows' own light-dismiss surfaces.
/// </summary>
/// <remarks>
/// It has two callers with different needs, which is why it is worth pinning. Opening a panel of
/// ours closes whichever of these is up; and the input language indicator ignores them, because a
/// panel is not somewhere anyone types.
/// </remarks>
public class ShellSurfaceTests
{
    /// <summary>
    /// Quick Settings, which is the panel the volume and network cluster raises.
    /// </summary>
    /// <remarks>
    /// Measured on Windows 11 25H2: its window really does carry class
    /// <c>ControlCenterWindow</c>, and its thread really does run a different keyboard layout from
    /// the user's — 0x0414 against 0x0809 here. That second fact is why the language indicator has
    /// to know about this table: following the foreground into Quick Settings made the indicator
    /// report Quick Settings' language, which looked exactly like the layout having changed.
    /// </remarks>
    [Fact]
    public void Quick_Settings_Is_Recognised() =>
        Assert.Equal(ShellSurface.QuickSettings, ShellSurfaceWatcher.SurfaceFor("ControlCenterWindow"));

    /// <summary>An ordinary application window is not a shell surface.</summary>
    /// <remarks>
    /// The important half of the table. A false positive here would have the bar dismissing a
    /// window the user was working in, and the language indicator refusing to name its language.
    /// </remarks>
    [Theory]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("MozillaWindowClass")]
    [InlineData("CabinetWClass")]
    [InlineData("Notepad")]
    [InlineData("")]
    public void An_Ordinary_Window_Is_Not_A_Surface(string className) =>
        Assert.Equal(ShellSurface.None, ShellSurfaceWatcher.SurfaceFor(className));
}
