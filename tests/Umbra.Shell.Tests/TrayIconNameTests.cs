using ManagedShell.WindowsTray;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// A notification icon with no tooltip used to be announced as its type name.
/// </summary>
/// <remarks>
/// The two cases are the two measured on the laptop with UI Automation: a packaged app in the overflow
/// flyout (no GUID, an executable that describes itself) and the power indicator in the strip
/// (a system GUID, hosted by Explorer). Every assertion below also checks the result is never
/// empty, because an empty automation name is exactly what WPF replaces with the type name.
/// </remarks>
public class TrayIconNameTests
{
    private const string Notes =
        @"C:\Program Files\WindowsApps\Contoso.Notes_2.42304.0.0_x64__pzs8sxrjxfjjc\app\notes.exe";

    private const string Explorer = @"C:\Windows\explorer.exe";

    private static string? Describe(string path) =>
        path.EndsWith("notes.exe", StringComparison.OrdinalIgnoreCase) ? "Notes"
        : path.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) ? "Windows Explorer"
        : null;

    private static string Name(string? title, Guid guid, string? path)
    {
        string name = TrayIconName.For(title, guid, path, Describe);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.DoesNotContain("ManagedShell", name);
        return name;
    }

    [Fact]
    public void The_Tooltip_Wins_When_There_Is_One() =>
        Assert.Equal("Bluetooth Devices", Name("Bluetooth Devices", Guid.Empty, Explorer));

    [Fact]
    public void An_Untitled_Application_Is_Called_What_It_Calls_Itself() =>
        Assert.Equal("Notes", Name("", Guid.Empty, Notes));

    /// <summary>Explorer's description would call the battery "Windows Explorer".</summary>
    [Fact]
    public void An_Untitled_System_Indicator_Gets_Windows_Name_For_It() =>
        Assert.Equal("Battery", Name(null, Guid.Parse(NotificationArea.POWER_GUID), Explorer));

    [Fact]
    public void An_Untitled_Unknown_Explorer_Icon_Does_Not_Borrow_Explorers_Name() =>
        Assert.Equal(TrayIconName.Fallback, Name("", Guid.NewGuid(), Explorer));

    [Fact]
    public void No_Description_Falls_Back_To_The_File_Name() =>
        Assert.Equal("ascent", Name(" ", Guid.Empty, @"C:\Program Files\Ascent\ascent.exe"));

    [Fact]
    public void Nothing_Known_Is_Still_Not_Empty() =>
        Assert.Equal(TrayIconName.Fallback, Name(null, Guid.Empty, null));
}
