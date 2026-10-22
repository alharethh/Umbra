using System.IO;
using System.Windows.Data;
using ManagedShell.WindowsTasks;
using Umbra.Shell.Tasks;
using Xunit;

namespace Umbra.Shell.Tests;

/// <summary>
/// Pinning a UWP application. Its window runs from <c>ApplicationFrameHost.exe</c>, so a pin taken
/// from the window's executable named every one of them "Application Frame Host" and, once closed,
/// started the frame host and nothing else. Reproduced on the laptop, 2026-10-22, with Settings.
/// </summary>
public sealed class PackagedPinTests : IDisposable
{
    private const string FrameHost = @"C:\Windows\System32\ApplicationFrameHost.exe";
    private const string SettingsId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"umbra-packaged-pin-{Guid.NewGuid():N}");
    private readonly string _pinFile;

    public PackagedPinTests()
    {
        Directory.CreateDirectory(_root);
        _pinFile = Path.Combine(_root, "pinned-apps.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>What every build before the fix wrote for a pinned Settings.</summary>
    private PinSettings SeededWithTheOldRecord()
    {
        File.WriteAllLines(_pinFile,
            ["# Pinned applications, in bar order. key|path|name", $"{SettingsId}|{FrameHost}|Application Frame Host"]);

        return new PinSettings(_pinFile);
    }

    private static PackagedAppNames Resolved(string name)
    {
        PackagedAppNames names = new(_ => name);
        using ManualResetEventSlim done = new();

        names.Request(SettingsId, done.Set);
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));

        return names;
    }

    [Fact]
    public void A_Frame_Hosted_Window_Pins_Its_Application_Not_The_Host()
    {
        PinnedApp pin = PinSettings.RecordFor(SettingsId, FrameHost, "Application Frame Host", "Settings", "Settings");

        Assert.Equal(@"shell:AppsFolder\" + SettingsId, pin.Path);
        Assert.Equal("Settings", pin.Name);
        Assert.True(pin.IsPackaged);
    }

    [Fact]
    public void Before_The_Shell_Answers_The_Title_Names_The_Pin()
    {
        PinnedApp pin = PinSettings.RecordFor(SettingsId, FrameHost, "Application Frame Host", "Settings", null);

        Assert.Equal("Settings", pin.Name);
    }

    /// <summary>
    /// The frame host is on disk, so the old pin's path looked launchable — that is the bug.
    /// </summary>
    [Fact]
    public void A_Packaged_Pin_Launches_By_Its_Id_Not_By_The_Host()
    {
        Assert.True(File.Exists(FrameHost));

        TaskbarSlot old = new(SettingsId, new PinnedApp(SettingsId, FrameHost, "Application Frame Host"));
        TaskbarSlot made = new(SettingsId, PinSettings.RecordFor(SettingsId, FrameHost, null, "Settings", "Settings"));

        Assert.Null(old.LaunchTarget);
        Assert.Null(made.LaunchTarget);
    }

    [Fact]
    public void A_Running_Frame_Does_Not_Heal_The_Pin_Back_To_The_Host()
    {
        File.WriteAllLines(_pinFile,
            ["# Pinned applications, in bar order. key|path|name", $"{SettingsId}|shell:AppsFolder\\{SettingsId}|Settings"]);
        PinSettings pins = new(_pinFile);

        Assert.False(pins.UpdatePath(SettingsId, FrameHost));
        Assert.Equal(@"shell:AppsFolder\" + SettingsId, pins.Pins[0].Path);
    }

    [Fact]
    public void An_Old_Pin_Is_Repaired_On_Disk_Once_And_Only_Once()
    {
        PinSettings pins = SeededWithTheOldRecord();

        Assert.True(pins.UpdatePackaged(SettingsId, "Settings"));
        Assert.False(pins.UpdatePackaged(SettingsId, "Settings"));

        PinnedApp reread = new PinSettings(_pinFile).Pins[0];
        Assert.Equal(@"shell:AppsFolder\" + SettingsId, reread.Path);
        Assert.Equal("Settings", reread.Name);
    }

    [Fact]
    public void Repair_Leaves_Ordinary_Pins_Alone()
    {
        File.WriteAllLines(_pinFile,
            ["# Pinned applications, in bar order. key|path|name", @"firefox|C:\Program Files\Mozilla Firefox\firefox.exe|Firefox"]);
        PinSettings pins = new(_pinFile);

        Assert.False(pins.UpdatePackaged("firefox", "Something else"));
        Assert.Equal("Firefox", pins.Pins[0].Name);
    }

    /// <summary>
    /// The case the backlog item named: a closed pin, with no window to be named from. The model
    /// names it from its key alone.
    /// </summary>
    [Fact]
    public void The_Model_Names_A_Closed_Packaged_Pin()
    {
        PinSettings pins = SeededWithTheOldRecord();
        CollectionView none = new(new List<ApplicationWindow>());

        using TaskbarModel model = new(none, pins, Resolved("Settings"));

        TaskbarSlot slot = Assert.Single(model.Slots);
        Assert.Equal("Settings", slot.DisplayName);
        Assert.Equal("Settings", new PinSettings(_pinFile).Pins[0].Name);
    }
}
