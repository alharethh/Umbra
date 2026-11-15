using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using ManagedShell.WindowsTasks;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// A pin of a packaged application keeps its icon and its launch after the application updates,
/// including while it is closed.
/// </summary>
/// <remarks>
/// <para>
/// Each package version installs into its own folder and the old one is deleted, so a pin made
/// under one version names a folder that is gone after the next. Reproduced on the laptop,
/// 2026-11-15, on master: a WhatsApp pin under 2.2600.50.0 with 2.2644.101.0 installed drew an
/// empty slot, and a click asked Windows to activate the old path and started nothing. The
/// owner's own Claude pin did the same on 2026-10-29 (bring-up.log).
/// </para>
/// <para>
/// The installs here are folders in a temp directory named as Windows names them, with a real
/// manifest and a stand-in executable, handed to the code through <see cref="IPackageFolders"/>
/// as Windows would hand over the installed versions of a family.
/// </para>
/// </remarks>
public sealed class PackagedPinUpdateTests : IDisposable
{
    private const string Family = "Fake.Chat_abcdefghjkmnp";
    private const string AppId = Family + "!App";

    private readonly string _apps = Path.Combine(Path.GetTempPath(), $"umbra-update-{Guid.NewGuid():N}", "WindowsApps");
    private readonly string _pinFile;

    public PackagedPinUpdateTests()
    {
        Directory.CreateDirectory(_apps);
        _pinFile = Path.Combine(Path.GetDirectoryName(_apps)!, "pinned-apps.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_apps)!, recursive: true); } catch (IOException) { }
    }

    private string Folder(string version, string resource = "") =>
        Path.Combine(_apps, $"Fake.Chat_{version}_x64_{resource}_abcdefghjkmnp");

    /// <summary>Where a version's executable is, whether or not that version is installed.</summary>
    private string Exe(string version, string name = "Chat.Root.exe") => Path.Combine(Folder(version), name);

    /// <summary>Installs a version: its folder, its manifest and its executable.</summary>
    private string Install(string version, string executable = "Chat.Root.exe", bool withApplication = true)
    {
        string folder = Folder(version);
        Directory.CreateDirectory(folder);

        string application = withApplication
            ? $"<Applications><Application Id=\"App\" Executable=\"{executable}\" EntryPoint=\"Windows.FullTrustApplication\" /></Applications>"
            : "";

        File.WriteAllText(Path.Combine(folder, "AppxManifest.xml"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Fake.Chat" Publisher="CN=Somebody" Version="{version}" ProcessorArchitecture="x64" />
              {application}
            </Package>
            """);

        if (withApplication)
        {
            string exe = Path.Combine(folder, executable);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, []);
        }
        return folder;
    }

    /// <summary>What Windows would list for the family: every installed version folder.</summary>
    private sealed class Installed(string apps) : IPackageFolders
    {
        public int Asked;

        public IEnumerable<string> FoldersOf(string familyName)
        {
            Interlocked.Increment(ref Asked);
            return Directory.Exists(apps)
                ? Directory.GetDirectories(apps).Where(d => AppIdentity.FamilyNameOf(Path.GetFileName(d)) == familyName)
                : [];
        }
    }

    private sealed class NothingListed : IPackageFolders
    {
        public IEnumerable<string> FoldersOf(string familyName) => [];
    }

    private sealed class Broken : IPackageFolders
    {
        public IEnumerable<string> FoldersOf(string familyName) => throw new InvalidOperationException("no package API");
    }

    private PackagedExecutable Packages(Func<long>? clock = null) => new(new Installed(_apps), clock);

    // ------------------------------------------------------------------ finding the installed version

    [Fact]
    public void A_Pin_Under_A_Deleted_Version_Finds_The_Installed_One()
    {
        Install("2.2644.101.0");

        PackagedExecutable.Located? located = Packages().Locate(Exe("2.2600.50.0"));

        Assert.NotNull(located);
        Assert.Equal(AppId, located.AppId);
        Assert.Equal(Exe("2.2644.101.0"), located.Executable, ignoreCase: true);
    }

    [Fact]
    public void A_Nested_Executable_Is_Found_At_The_Same_Place_In_The_New_Version()
    {
        Install("5.11492.5.0", @"app\claude.exe");

        PackagedExecutable.Located? located = Packages().Locate(Path.Combine(Folder("4.23118.0.0"), "app", "claude.exe"));

        Assert.Equal(Path.Combine(Folder("5.11492.5.0"), "app", "claude.exe"), located?.Executable, ignoreCase: true);
    }

    [Fact]
    public void The_Highest_Installed_Version_Wins()
    {
        Install("2.2644.101.0");
        Install("2.10.0.0");      // older, despite sorting after as text
        Install("2.2700.0.0");

        Assert.Equal(Exe("2.2700.0.0"), Packages().Locate(Exe("2.2600.50.0"))?.Executable, ignoreCase: true);
    }

    /// <summary>A resource or framework package of the same family has no applications to run.</summary>
    [Fact]
    public void A_Same_Family_Package_With_No_Application_Is_Passed_Over()
    {
        Install("2.2644.101.0");
        Install("9.0.0.0", withApplication: false);

        Assert.Equal(Exe("2.2644.101.0"), Packages().Locate(Exe("2.2600.50.0"))?.Executable, ignoreCase: true);
    }

    /// <summary>
    /// A newer version whose manifest is there but whose executable is not (staged, half written)
    /// is not one to point a pin at: the click would start nothing.
    /// </summary>
    [Fact]
    public void A_Version_Without_Its_Executable_Is_Passed_Over()
    {
        Install("2.2644.101.0");
        Install("2.2700.0.0");
        File.Delete(Exe("2.2700.0.0"));

        Assert.Equal(Exe("2.2644.101.0"), Packages().Locate(Exe("2.2600.50.0"))?.Executable, ignoreCase: true);
    }

    /// <summary>An update may rename the executable; the manifest names the one that runs now.</summary>
    [Fact]
    public void A_Renamed_Executable_Is_Taken_From_The_New_Manifest()
    {
        Install("3.0.0.0", "Chat.exe");

        PackagedExecutable.Located? located = Packages().Locate(Exe("2.2600.50.0"));

        Assert.Equal(AppId, located?.AppId);
        Assert.Equal(Exe("3.0.0.0", "Chat.exe"), located?.Executable, ignoreCase: true);
    }

    [Fact]
    public void Without_Windows_Answering_The_Recorded_Folder_Still_Counts()
    {
        Install("2.2644.101.0");

        Assert.Equal(AppId, new PackagedExecutable(new NothingListed()).AppIdFor(Exe("2.2644.101.0")));
        Assert.Equal(AppId, new PackagedExecutable(new Broken()).AppIdFor(Exe("2.2644.101.0")));
    }

    [Fact]
    public void An_Uninstalled_Application_Resolves_To_Nothing_And_Is_Not_Asked_About_On_Every_Rebuild()
    {
        long now = 0;
        Installed windows = new(_apps);
        PackagedExecutable packages = new(windows, () => now);

        Assert.Null(packages.Locate(Exe("2.2600.50.0")));
        Assert.Null(packages.Locate(Exe("2.2600.50.0")));
        Assert.Equal(1, windows.Asked);

        // Installed later (or caught mid-update): found once the short retry has passed.
        Install("2.2644.101.0");
        now += 31_000;
        Assert.Equal(AppId, packages.AppIdFor(Exe("2.2600.50.0")));
    }

    /// <summary>A remembered answer is only good while its executable exists: the next update deletes it.</summary>
    [Fact]
    public void A_Remembered_Answer_Is_Dropped_When_Its_Version_Is_Deleted()
    {
        PackagedExecutable packages = Packages();
        string first = Install("2.2644.101.0");
        Assert.Equal(Exe("2.2644.101.0"), packages.Locate(Exe("2.2600.50.0"))?.Executable, ignoreCase: true);

        Install("2.2700.0.0");
        Directory.Delete(first, recursive: true);

        Assert.Equal(Exe("2.2700.0.0"), packages.Locate(Exe("2.2600.50.0"))?.Executable, ignoreCase: true);
    }

    [Fact]
    public void An_Ordinary_Executable_Is_Not_Looked_Up()
    {
        Installed windows = new(_apps);
        Assert.Null(new PackagedExecutable(windows).Locate(Path.Combine(Environment.SystemDirectory, "notepad.exe")));
        Assert.Equal(0, windows.Asked);
    }

    [Theory]
    [InlineData("Fake.Chat_2.2644.101.0_x64__abcdefghjkmnp", "2.2644.101.0")]
    [InlineData("Discord", null)]
    public void The_Version_Is_Read_From_The_Folder_Name(string folder, string? version) =>
        Assert.Equal(version is null ? null : Version.Parse(version), AppIdentity.VersionOf(folder));

    /// <summary>
    /// The real API, against a package every Windows 11 install has: the folder it names holds the
    /// package's manifest. Catches a broken P/Invoke, which would otherwise only show as pins that
    /// never move.
    /// </summary>
    [Fact]
    public void Windows_Names_The_Installed_Folder_Of_A_Real_Package()
    {
        string folder = Assert.Single(new InstalledPackages().FoldersOf("windows.immersivecontrolpanel_cw5n1h2txyewy"));

        Assert.True(File.Exists(Path.Combine(folder, "AppxManifest.xml")), folder);
    }

    // ------------------------------------------------------------------ the slot

    [Fact]
    public void A_Stale_Packaged_Pin_Draws_Its_Package_Logo_And_Launches_The_Installed_Version()
    {
        Install("2.2644.101.0");
        string old = Exe("2.2600.50.0");
        RecordingSource source = new();

        TaskbarSlot slot = new(old, new PinnedApp(old, old, "Chat"),
            new ShortcutResolver(_apps), new AppIconResolver(source), packages: Packages());

        _ = slot.Icon;

        Assert.True(Eventually(() => source.Asked.Count >= 1));
        Assert.Equal($@"shell:AppsFolder\{AppId}", source.Asked[0]);

        Assert.Equal(Exe("2.2644.101.0"), slot.LaunchTarget?.Path, ignoreCase: true);
        Assert.Equal(AppId, slot.ActivationId);
    }

    [Fact]
    public void A_Working_Pin_Launches_Its_Own_Executable_As_Before()
    {
        string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        TaskbarSlot slot = new(exe, new PinnedApp(exe, exe, "Notepad"), new ShortcutResolver(_apps), packages: Packages());

        Assert.Equal(exe, slot.LaunchTarget?.Path);
        Assert.Equal(exe, slot.ActivationId);
    }

    // ------------------------------------------------------------------ the model moves the pin

    private PinSettings Pins(params (string Key, string Path, string Name)[] pins)
    {
        File.WriteAllLines(_pinFile,
            ["# Pinned applications, in bar order. key|path|name", .. pins.Select(p => $"{p.Key}|{p.Path}|{p.Name}")]);
        return new PinSettings(_pinFile);
    }

    private static PackagedAppNames Named(string name) => new(_ => name);

    private static CollectionView NoWindows() => new(new List<ApplicationWindow>());

    /// <summary>
    /// Moved whole and in place: identity, path, name and position in the row, and written to the
    /// file, so a restart starts from the installed version.
    /// </summary>
    [Fact]
    public void The_Model_Moves_A_Stale_Packaged_Pin_To_The_Installed_Version_In_Place()
    {
        Install("2.2644.101.0");
        string old = Exe("2.2600.50.0");
        string now = Exe("2.2644.101.0");
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        PinSettings pins = Pins((notepad, notepad, "Notepad"), (old, old, "Chat"), (@"C:\x\y.exe", @"C:\x\y.exe", "Y"));

        using TaskbarModel model = new(NoWindows(), pins, Named("Chat"), Packages());

        Assert.Equal([notepad, now, @"C:\x\y.exe"], model.Slots.Select(s => s.Key), StringComparer.OrdinalIgnoreCase);

        PinnedApp moved = new PinSettings(_pinFile).Pins[1];
        Assert.Equal(now, moved.Key, ignoreCase: true);
        Assert.Equal(now, moved.Path, ignoreCase: true);
    }

    /// <summary>The trap: moving only the path would leave the old identity, and a second slot once it ran.</summary>
    [Fact]
    public void A_Moved_Pin_Is_The_Slot_The_Running_Application_Will_Join()
    {
        Install("2.2644.101.0");
        string old = Exe("2.2600.50.0");

        using TaskbarModel model = new(NoWindows(), Pins((old, old, "Chat")), Named("Chat"), Packages());

        TaskbarSlot slot = Assert.Single(model.Slots);
        Assert.True(AppIdentity.Same(slot.Key, slot.Pin!.Path), "key and path differ, so a running window would not join this slot");
        Assert.True(AppIdentity.Same(slot.Key, Exe("2.2644.101.0")));
    }

    [Fact]
    public void A_Working_Pin_And_An_Uninstalled_One_Are_Left_Alone()
    {
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        string gone = Exe("2.2600.50.0");
        PinSettings pins = Pins((notepad, notepad, "Notepad"), (gone, gone, "Chat"));
        DateTime written = File.GetLastWriteTimeUtc(_pinFile);

        using TaskbarModel model = new(NoWindows(), pins, Named("Chat"), Packages());

        Assert.Equal([notepad, gone], new PinSettings(_pinFile).Pins.Select(p => p.Key));
        Assert.Equal(written, File.GetLastWriteTimeUtc(_pinFile));
    }

    /// <summary>A pin made before #54 is named after its file; it takes the Apps folder's name.</summary>
    [Fact]
    public void A_Packaged_Pin_Named_After_Its_File_Takes_Its_Apps_Folder_Name()
    {
        Install("2.2644.101.0");
        string exe = Exe("2.2644.101.0");
        PackagedAppNames names = Named("Chat");

        using ManualResetEventSlim done = new();
        names.Request(AppId, done.Set);
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));

        using TaskbarModel model = new(NoWindows(), Pins((exe, exe, "Chat.Root")), names, Packages());

        Assert.Equal("Chat", new PinSettings(_pinFile).Pins[0].Name);
    }

    private static bool Eventually(Func<bool> condition, int milliseconds = 5000)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private sealed class RecordingSource : IIconSource
    {
        private readonly ConcurrentQueue<string> _asked = new();
        public IReadOnlyList<string> Asked => [.. _asked];

        public ImageSource? Extract(IconCandidate candidate)
        {
            _asked.Enqueue(candidate.Path ?? "");
            return null;
        }
    }
}
