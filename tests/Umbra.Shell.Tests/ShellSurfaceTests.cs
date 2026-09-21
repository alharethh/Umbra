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

    /// <summary>
    /// The notification centre, which has no class of its own.
    /// </summary>
    /// <remarks>
    /// Measured on 25H2 by opening it and reading the foreground window: class
    /// <c>Windows.UI.Core.CoreWindow</c>, process <c>ShellExperienceHost</c>, title "Notification
    /// Center". The table used to say <c>NotificationCenterWindow</c>, which nothing answers to,
    /// so the clock's panel was never dismissed — it stayed in front and the tray flyout drew
    /// underneath it, which is how this was reported.
    /// </remarks>
    [Fact]
    public void The_Notification_Centre_Is_Recognised_By_Class_And_Host() =>
        Assert.Equal(
            ShellSurface.NotificationCentre,
            ShellSurfaceWatcher.SurfaceFor("Windows.UI.Core.CoreWindow", "ShellExperienceHost"));

    /// <summary>
    /// <b>The dangerous half.</b> A bare <c>CoreWindow</c> must never match.
    /// </summary>
    /// <remarks>
    /// <c>Windows.UI.Core.CoreWindow</c> is the generic UWP class — Settings, the input host and
    /// every XAML island on the desktop carry it. Dismissal is a <i>toggle</i>, so a match here
    /// would not merely fail to close something, it would <b>open</b> the notification centre on
    /// top of whatever the user was working in. Drop the process from the pattern and these go
    /// red while the test above still passes, which is the point of having both.
    /// </remarks>
    [Theory]
    [InlineData("SystemSettings")]
    [InlineData("TextInputHost")]
    [InlineData("explorer")]
    [InlineData("ShellHost")]
    [InlineData(null)]
    public void A_Bare_CoreWindow_Is_Not_The_Notification_Centre(string? processName) =>
        Assert.Equal(
            ShellSurface.None,
            ShellSurfaceWatcher.SurfaceFor("Windows.UI.Core.CoreWindow", processName));

    /// <summary>Quick Settings keeps its own class, and has changed host between builds.</summary>
    /// <remarks>
    /// It was <c>ShellExperienceHost</c> and is <c>ShellHost</c> on 25H2, so qualifying it by
    /// process would break on the next move. Only the ambiguous one is qualified.
    /// </remarks>
    [Theory]
    [InlineData("ShellHost")]
    [InlineData("ShellExperienceHost")]
    [InlineData(null)]
    public void Quick_Settings_Does_Not_Depend_On_Its_Host(string? processName) =>
        Assert.Equal(
            ShellSurface.QuickSettings,
            ShellSurfaceWatcher.SurfaceFor("ControlCenterWindow", processName));
}
