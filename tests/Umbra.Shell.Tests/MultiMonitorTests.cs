using System.IO;
using Umbra.Shell.Settings;
using Umbra.Shell.Wear;

namespace Umbra.Shell.Tests;

/// <summary>
/// A bar on every monitor: the setting, and the one thing about it that fails silently.
/// </summary>
/// <remarks>
/// The screen enumeration and the AppBar registrations need a desktop and are verified by
/// running the bar. What is testable — and what would lose data rather than merely look wrong —
/// is the per-screen wear dataset path.
/// </remarks>
public class MultiMonitorTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"umbra-mm-{Guid.NewGuid():N}.txt");

    /// <summary>Off by default, for the reason recorded against the setting.</summary>
    /// <remarks>
    /// Not caution. §11.2 Q12: a non-OLED second screen pays the dim tiers, the monochrome row
    /// and the shrunken icons in legibility and gets nothing back, because an LCD does not wear
    /// differentially. Until the treatment can differ per monitor, off is the honest default.
    /// </remarks>
    [Fact]
    public void A_Bar_On_Every_Monitor_Is_Off_By_Default() =>
        Assert.False(new UmbraSettings().BarOnEveryMonitor);

    [Fact]
    public void The_Setting_Survives_A_Round_Trip()
    {
        UmbraSettings original = new() { BarOnEveryMonitor = true };
        original.Save(_path);

        Assert.True(UmbraSettings.Load(_path).BarOnEveryMonitor);
    }

    /// <summary>
    /// It needs a restart, and saying otherwise is worse than saying nothing.
    /// </summary>
    /// <remarks>
    /// The bars and their AppBar registrations are built at startup. A settings window that
    /// reported "applied" would leave the user watching for a second bar that is not coming.
    /// </remarks>
    [Fact]
    public void Changing_It_Needs_A_Restart() =>
        Assert.True(new UmbraSettings().NeedsRestartToReach(new UmbraSettings { BarOnEveryMonitor = true }));

    /// <summary>
    /// <b>One dataset per screen, or both are lost.</b>
    /// </summary>
    /// <remarks>
    /// The accumulator is sized to the reserved rectangle in physical pixels, so two screens of
    /// different size or orientation are two incompatible grids. Share one file and
    /// <c>WearStore.LoadCompatible</c> rightly refuses to load the other bar's data — and then
    /// this bar saves over it. Every restart, silently, in both directions. Make
    /// <c>PathForScreen</c> ignore the device name and this goes red.
    /// </remarks>
    [Fact]
    public void Each_Screen_Gets_Its_Own_Dataset()
    {
        string one = WearSampler.PathForScreen(@"\\.\DISPLAY1", primary: false);
        string two = WearSampler.PathForScreen(@"\\.\DISPLAY2", primary: false);

        Assert.NotEqual(one, two);
    }

    /// <summary>
    /// The primary keeps the plain file, so existing history is not orphaned.
    /// </summary>
    /// <remarks>
    /// Someone who has been running one bar for weeks and ticks this box should not silently
    /// start a new dataset on the screen they were already collecting.
    /// </remarks>
    [Fact]
    public void The_Primary_Keeps_The_Original_File()
    {
        Assert.Equal(WearSampler.DefaultPath, WearSampler.PathForScreen(@"\\.\DISPLAY1", primary: true));
        Assert.Equal(WearSampler.DefaultPath, WearSampler.PathForScreen(null, primary: true));
        Assert.Equal(WearSampler.DefaultPath, WearSampler.PathForScreen(null, primary: false));
    }

    /// <summary>
    /// A device name is not a file name.
    /// </summary>
    /// <remarks>
    /// <c>\\.\DISPLAY2</c> carries a drive separator and directory separators. Used raw it either
    /// throws or writes somewhere nobody expects; either way the dataset for the second screen
    /// never appears and nothing says why.
    /// </remarks>
    [Fact]
    public void A_Device_Name_Is_Made_Safe_For_A_Path()
    {
        string path = WearSampler.PathForScreen(@"\\.\DISPLAY2", primary: false);
        string name = Path.GetFileName(path);

        Assert.Equal(Path.GetDirectoryName(WearSampler.DefaultPath), Path.GetDirectoryName(path));
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
        Assert.Contains("DISPLAY2", name);
        Assert.EndsWith(".dat", name);

        // And it is a path the filesystem will actually accept.
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }
}
