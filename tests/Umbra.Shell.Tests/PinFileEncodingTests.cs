using System.IO;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// A pin's name can be a window title, and a window title can hold a lone surrogate.
/// </summary>
/// <remarks>
/// Same trap as <see cref="BringUpLogTests"/>: <c>File.WriteAllLines</c>'s UTF-8 encoder throws
/// on one, and the pin file caught only I/O and access errors, so saving a pin named after
/// Windows Terminal's truncated title would have ended the bar. The name falls back to the title
/// when an application has no file description, and a packaged pin keeps its title until the
/// shell answers with the real name.
/// </remarks>
public class PinFileEncodingTests : IDisposable
{
    private const string Key = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"umbra-pin-encoding-{Guid.NewGuid():N}.txt");

    [Fact]
    public void A_Name_With_A_Lone_Surrogate_Is_Saved_Rather_Than_Thrown()
    {
        File.WriteAllLines(_path,
            ["# Pinned applications, in bar order. key|path|name",
             $"{Key}|{PinnedApp.AppsFolderPrefix}{Key}|Terminal"]);

        PinSettings pins = new(_path);

        Assert.True(pins.UpdatePackaged(Key, "Terminal \uD83D"));

        PinnedApp reloaded = Assert.Single(new PinSettings(_path).Pins);
        Assert.Equal("Terminal �", reloaded.Name);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }
}
