using System.IO;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// A pinned application that has moved since it was pinned.
/// </summary>
/// <remarks>
/// <para>
/// A pin records the executable path of the window it was created from, and that path is not
/// stable. Applications that update in place install each release into its own versioned directory
/// and delete the old one — so a pin taken months ago points at nothing. A pinned application that
/// is not running has <b>only</b> that path to work from, so it draws as an empty placeholder and
/// does nothing when clicked.
/// </para>
/// <para>
/// Observed exactly that way on the development machine: Discord's pin held
/// <c>app-1.0.9253\Discord.exe</c> while the disk had moved on to <c>app-1.0.9258</c>. The icon was
/// reported as "a weird logo to the right of firefox".
/// </para>
/// </remarks>
public class StalePinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"umbra-stale-{Guid.NewGuid():N}");
    private readonly string _pinFile;

    public StalePinTests()
    {
        Directory.CreateDirectory(_root);
        _pinFile = Path.Combine(_root, "pinned-apps.txt");
    }

    private PinSettings Seeded(string key, string path, string name)
    {
        File.WriteAllLines(_pinFile,
            ["# Pinned applications, in bar order. key|path|name", $"{key}|{path}|{name}"]);

        return new PinSettings(_pinFile);
    }

    [Fact]
    public void ARunningApplicationHealsItsOwnPin()
    {
        PinSettings pins = Seeded("discord", @"C:\Local\Discord\app-1.0.9253\Discord.exe", "Discord");

        Assert.True(pins.UpdatePath("discord", @"C:\Local\Discord\app-1.0.9258\Discord.exe"));
        Assert.Equal(@"C:\Local\Discord\app-1.0.9258\Discord.exe", pins.Pins[0].Path);
    }

    /// <summary>
    /// The heal survives a restart, which is the entire point of it.
    /// </summary>
    /// <remarks>
    /// Healing in memory only would fix the bar for as long as the application stayed open and lose
    /// it again the moment the user logged out — the exact conditions under which nobody would
    /// connect the symptom to the cause.
    /// </remarks>
    [Fact]
    public void TheHealIsWrittenToDisk()
    {
        PinSettings pins = Seeded("discord", @"C:\Local\Discord\app-1.0.9253\Discord.exe", "Discord");
        pins.UpdatePath("discord", @"C:\Local\Discord\app-1.0.9258\Discord.exe");

        Assert.Equal(@"C:\Local\Discord\app-1.0.9258\Discord.exe", new PinSettings(_pinFile).Pins[0].Path);
    }

    /// <summary>
    /// An unchanged path does not rewrite the file.
    /// </summary>
    /// <remarks>
    /// This runs inside the model's rebuild, which happens on every window event anywhere on the
    /// desktop. Reporting a change when there is none would put a disk write behind every one.
    /// </remarks>
    [Fact]
    public void AnUnchangedPathChangesNothing()
    {
        PinSettings pins = Seeded("discord", @"C:\Local\Discord\app.exe", "Discord");

        Assert.False(pins.UpdatePath("discord", @"C:\Local\Discord\app.exe"));
        Assert.False(pins.UpdatePath("discord", @"c:\local\discord\APP.exe"));
        Assert.False(pins.UpdatePath("discord", null));
        Assert.False(pins.UpdatePath("discord", "   "));
    }

    [Fact]
    public void HealingSomethingThatIsNotPinnedIsIgnored()
    {
        PinSettings pins = Seeded("discord", @"C:\Local\Discord\app.exe", "Discord");

        Assert.False(pins.UpdatePath("something-else", @"C:\elsewhere.exe"));
        Assert.Equal(@"C:\Local\Discord\app.exe", pins.Pins[0].Path);
    }

    /// <summary>
    /// A shortcut is found by its own name, not by what it points at.
    /// </summary>
    /// <remarks>
    /// <b>This is the part that is counter-intuitive.</b> Matching on the target is the obvious
    /// choice and is the one that does not work: Discord's Start Menu shortcut targets
    /// <c>Update.exe</c>, a launcher that survives releases, which is nothing like the
    /// <c>Discord.exe</c> the pin recorded. The name is what connects them.
    /// </remarks>
    [Fact]
    public void AnApplicationIsLookedUpByNameAndByExecutableStem()
    {
        string?[] names = [.. ShortcutResolver.Names("Discord", @"C:\Local\Discord\app-1.0.9253\Discord.exe")];

        Assert.Contains("Discord", names);
        Assert.DoesNotContain("Discord.exe", names);
        Assert.DoesNotContain("Update.exe", names);
    }

    [Fact]
    public void TheExecutableStemIsUsedWhenThereIsNoDisplayName()
    {
        string?[] names = [.. ShortcutResolver.Names(null, @"C:\Program Files\Mozilla Firefox\firefox.exe")];

        Assert.Contains("firefox", names);
    }

    [Fact]
    public void NothingKnownYieldsNoNames()
    {
        Assert.Empty(ShortcutResolver.Names(null, null));
        Assert.Empty(ShortcutResolver.Names("  ", ""));
    }

    [Fact]
    public void AnEmptyStartMenuResolvesNothing()
    {
        ShortcutResolver resolver = new(Path.Combine(_root, "no-such-start-menu"));

        Assert.Null(resolver.Resolve("Discord", @"C:\Local\Discord\Discord.exe"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
