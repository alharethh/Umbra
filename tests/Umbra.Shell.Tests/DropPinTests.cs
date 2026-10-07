using System.IO;
using Umbra.Shell.Shell;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// Dragging an executable, or a shortcut to one, from Explorer onto the bar pins it.
/// </summary>
/// <remarks>
/// Every failure here is silent. A shortcut with arguments pinned as its bare target launches
/// something other than what was dropped; a dropped executable pinned under its path while the
/// application runs under an app user model ID draws the application twice; dropping something
/// already pinned pins it again beside itself. None of them throws.
/// </remarks>
public class DropPinTests : IDisposable
{
    private static readonly string Notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"umbra-drop-{Guid.NewGuid():N}")).FullName;

    private string PinFile => Path.Combine(_dir, "pins.txt");

    private string Touch(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private string Shortcut(string name, string target, string arguments = "")
    {
        string path = Path.Combine(_dir, name);

        object link = ShellLinkInterop.Create()!;
        ((ShellLinkInterop.IShellLinkW)link).SetPath(target);
        if (arguments.Length > 0) ((ShellLinkInterop.IShellLinkW)link).SetArguments(arguments);
        ((ShellLinkInterop.IPersistFile)link).Save(path, true);

        return path;
    }

    [Fact]
    public void An_Executable_Pins_Itself_And_Nothing_Else_Does()
    {
        string exe = Touch("tool.exe");
        string text = Touch("notes.txt");
        string missing = Path.Combine(_dir, "gone.exe");

        Assert.Equal([exe], DroppedExecutables.From([text, exe, missing]));
    }

    [Fact]
    public void The_Same_Executable_Twice_Is_Pinned_Once()
    {
        string exe = Touch("tool.exe");
        string link = Shortcut("tool.lnk", exe);

        Assert.Equal([exe], DroppedExecutables.From([exe, link, exe.ToUpperInvariant()]));
    }

    [Fact]
    public void A_Shortcut_Pins_What_It_Points_At()
    {
        string link = Shortcut("Notepad.lnk", Notepad);

        string pinned = Assert.Single(DroppedExecutables.From([link]));
        Assert.True(AppIdentity.Same(Notepad, pinned));
    }

    /// <summary>
    /// A pin carries a path and nothing else, so arguments would be dropped on the floor.
    /// </summary>
    /// <remarks>
    /// Discord's own shortcut is <c>Update.exe --processStart Discord.exe</c>: accepting it would
    /// pin the updater.
    /// </remarks>
    [Fact]
    public void A_Shortcut_With_Arguments_Is_Not_Pinned()
    {
        string link = Shortcut("Discord.lnk", Notepad, "--processStart Discord.exe");

        Assert.Empty(DroppedExecutables.From([link]));
    }

    [Fact]
    public void A_Shortcut_To_Something_Other_Than_A_Program_Is_Not_Pinned()
    {
        string link = Shortcut("Notes.lnk", Touch("notes.txt"));

        Assert.Empty(DroppedExecutables.From([link]));
    }

    private const string Firefox = @"C:\Program Files\Mozilla Firefox\firefox.exe";
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    [Fact]
    public void A_Running_Application_With_Its_Own_Identity_Lends_It_To_The_Pin()
    {
        (string, string?)[] running = [("308046B0AF4A39CB", Firefox), (@"C:\x\other.exe", @"C:\x\other.exe")];

        Assert.Equal("308046B0AF4A39CB", AppIdentity.RunningIdentityFor(Firefox, running));
    }

    [Fact]
    public void Nothing_To_Adopt_When_The_Application_Is_Keyed_By_Its_Path_Or_Not_Running()
    {
        Assert.Null(AppIdentity.RunningIdentityFor(Firefox, [(Firefox, Firefox)]));
        Assert.Null(AppIdentity.RunningIdentityFor(Firefox, [(@"C:\x\other.exe", @"C:\x\other.exe")]));
    }

    /// <summary>
    /// Chrome's browser and its installed web applications are all <c>chrome.exe</c>.
    /// </summary>
    [Fact]
    public void An_Executable_Running_Under_Several_Identities_Adopts_None_Of_Them()
    {
        (string, string?)[] running = [("Chrome", Chrome), ("Chrome._crx_youtube", Chrome)];

        Assert.Null(AppIdentity.RunningIdentityFor(Chrome, running));
    }

    [Fact]
    public void Dropping_An_Executable_Records_Its_Path_And_Its_Name()
    {
        PinSettings pins = new(PinFile);

        Assert.Equal(Notepad, pins.PinExecutable(Notepad, Notepad));

        PinnedApp pin = Assert.Single(new PinSettings(PinFile).Pins);
        Assert.Equal(Notepad, pin.Path);
        Assert.False(string.IsNullOrWhiteSpace(pin.Name));
        Assert.NotEqual(Notepad, pin.Name);
    }

    /// <summary>
    /// Pinned from its window, an application with an app user model ID has a key that is not a
    /// path, so only the recorded path can say the dropped file is already there.
    /// </summary>
    [Fact]
    public void Dropping_Something_Already_Pinned_Under_Another_Identity_Does_Not_Pin_It_Again()
    {
        File.WriteAllLines(PinFile, ["# header", $"308046B0AF4A39CB|{Firefox}|Firefox"]);
        PinSettings pins = new(PinFile);

        Assert.Equal("308046B0AF4A39CB", pins.PinExecutable(Firefox, Firefox));
        Assert.Single(pins.Pins);
    }

    [Fact]
    public void Adopting_An_Identity_Keeps_The_Executable()
    {
        File.WriteAllLines(PinFile, ["# header", $"{Firefox}|{Firefox}|Firefox"]);
        PinSettings pins = new(PinFile);

        Assert.True(pins.Rekey(Firefox, "308046B0AF4A39CB", Firefox));

        PinnedApp pin = Assert.Single(new PinSettings(PinFile).Pins);
        Assert.Equal("308046B0AF4A39CB", pin.Key);
        Assert.Equal(Firefox, pin.Path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
