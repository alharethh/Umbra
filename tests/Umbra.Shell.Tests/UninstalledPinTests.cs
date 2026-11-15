using System.IO;
using System.Windows.Data;
using ManagedShell.WindowsTasks;
using Umbra.Shell.Shell;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// A pin whose application was uninstalled is removed, as Windows removes it, once the
/// application has been seen gone twice a minute apart.
/// </summary>
/// <remarks>
/// Reproduced on master, 2026-11-15: three such pins (an ordinary application, a Store desktop
/// application, a UWP application) kept their places in the row as empty slots that drew nothing
/// for at least 75 s and started nothing. The owner's call: remove them once confirmed, every kind.
/// </remarks>
public sealed class UninstalledPinTests : IDisposable
{
    private const string GoneFamily = "Contoso.Gone_8wekyb3d8bbwe";
    private const string LiveFamily = "Contoso.Live_8wekyb3d8bbwe";
    private const long Minute = 60_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"umbra-uninstalled-{Guid.NewGuid():N}");
    private readonly string _startMenu;
    private readonly string _pinFile;

    public UninstalledPinTests()
    {
        _startMenu = Path.Combine(_dir, "Start Menu");
        Directory.CreateDirectory(_startMenu);
        _pinFile = Path.Combine(_dir, "pinned-apps.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Notepad => Path.Combine(Environment.SystemDirectory, "notepad.exe");

    private string Touch(string relative)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    private string Missing(string relative)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private void Shortcut(string name, string target)
    {
        object link = ShellLinkInterop.Create()!;
        ((ShellLinkInterop.IShellLinkW)link).SetPath(target);
        ((ShellLinkInterop.IPersistFile)link).Save(Path.Combine(_startMenu, name), true);
    }

    /// <summary>Package families as Windows would answer for them: installed, not, or no answer.</summary>
    private sealed class Families : IPackageFolders
    {
        public IEnumerable<string> FoldersOf(string familyName) => [];

        public bool? AnyInstalled(string familyName) => familyName switch
        {
            LiveFamily => true,
            GoneFamily => false,
            _ => null,
        };
    }

    /// <summary>A probe whose Start Menu has been read already, as it has by the time a pin is looked at twice.</summary>
    private PinPresence Presence()
    {
        ShortcutResolver shortcuts = new(_startMenu);
        shortcuts.Resolve(null, null);
        return new PinPresence(new PackagedExecutable(new Families()), shortcuts);
    }

    private static string StoreExe(string family, string version) =>
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Program Files", "WindowsApps",
            $"{family.Split('_')[0]}_{version}_x64__{family.Split('_')[1]}", "App.exe");

    // ------------------------------------------------------------------ one look

    [Fact]
    public void An_Application_Whose_File_Is_There_Is_Present()
    {
        Assert.Equal(Tasks.Presence.Present, Presence().Of(new PinnedApp(Notepad, Notepad, "Notepad")));
    }

    [Fact]
    public void An_Application_With_Nothing_Left_Is_Gone()
    {
        string exe = Missing(@"Programs\GoneApp\GoneApp.exe");

        Assert.Equal(Tasks.Presence.Gone, Presence().Of(new PinnedApp(exe, exe, "GoneApp")));
    }

    /// <summary>Discord's case: the versioned file went, the launcher the Start Menu names did not.</summary>
    [Fact]
    public void A_Start_Menu_Shortcut_That_Still_Starts_Something_Keeps_It()
    {
        string exe = Missing(@"Programs\Chat\app-1.0\Chat.exe");
        Shortcut("Chat.lnk", Touch(@"Programs\Chat\Update.exe"));

        Assert.Equal(Tasks.Presence.Present, Presence().Of(new PinnedApp(exe, exe, "Chat")));
    }

    /// <summary>
    /// The shortcut index is read once per run and never refreshed, so an application uninstalled
    /// while the bar runs is still in it. Its target is what says it is gone.
    /// </summary>
    [Fact]
    public void A_Shortcut_Whose_Target_Is_Gone_Does_Not_Keep_It()
    {
        string exe = Missing(@"Programs\Chat\app-1.0\Chat.exe");
        string launcher = Touch(@"Programs\Chat\Update.exe");
        Shortcut("Chat.lnk", launcher);
        PinPresence presence = Presence();

        File.Delete(launcher);

        Assert.Equal(Tasks.Presence.Gone, presence.Of(new PinnedApp(exe, exe, "Chat")));
    }

    [Fact]
    public void The_Same_File_In_A_Sibling_Version_Folder_Keeps_It()
    {
        string exe = Missing(@"Programs\Chat\app-1.0.9253\Chat.exe");
        Touch(@"Programs\Chat\app-1.0.9258\Chat.exe");

        Assert.Equal(Tasks.Presence.Present, Presence().Of(new PinnedApp(exe, exe, "Chat")));
    }

    /// <summary>A USB stick, a network share or a locked volume comes back. Never removed for being away.</summary>
    [Fact]
    public void A_Path_On_A_Drive_That_Is_Not_There_Is_Unknown()
    {
        HashSet<char> used = [.. DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0]))];
        char letter = "ZYXWVUTSRQPONMLKJIHG".First(c => !used.Contains(c));
        string exe = $@"{letter}:\Portable\Tool\Tool.exe";

        Assert.Equal(Tasks.Presence.Unknown, Presence().Of(new PinnedApp(exe, exe, "Tool")));
    }

    [Fact]
    public void A_Store_Application_Is_Judged_By_Its_Family_Not_Its_Folder()
    {
        // Neither version folder exists; only the family's answer differs.
        string gone = StoreExe(GoneFamily, "1.0.0.0");
        string live = StoreExe(LiveFamily, "1.0.0.0");
        string unknown = StoreExe("Contoso.Silent_8wekyb3d8bbwe", "1.0.0.0");

        Assert.Equal(Tasks.Presence.Gone, Presence().Of(new PinnedApp(gone, gone, "Gone")));
        Assert.Equal(Tasks.Presence.Present, Presence().Of(new PinnedApp(live, live, "Live")));
        Assert.Equal(Tasks.Presence.Unknown, Presence().Of(new PinnedApp(unknown, unknown, "Silent")));
    }

    [Fact]
    public void A_Uwp_Application_Is_Judged_By_The_Family_In_Its_App_Id()
    {
        PinnedApp Uwp(string family) => new($"{family}!App", $@"{PinnedApp.AppsFolderPrefix}{family}!App", "App");

        Assert.Equal(Tasks.Presence.Gone, Presence().Of(Uwp(GoneFamily)));
        Assert.Equal(Tasks.Presence.Present, Presence().Of(Uwp(LiveFamily)));
        Assert.Equal(Tasks.Presence.Unknown, Presence().Of(Uwp("Contoso.Silent_8wekyb3d8bbwe")));
    }

    /// <summary>Reading every .lnk on the machine is never done on the caller's thread.</summary>
    [Fact]
    public void Before_The_Start_Menu_Is_Read_The_Answer_Is_Unknown()
    {
        string exe = Missing(@"Programs\GoneApp\GoneApp.exe");
        ShortcutResolver shortcuts = new(_startMenu);
        PinPresence presence = new(new PackagedExecutable(new Families()), shortcuts);

        Assert.Equal(Tasks.Presence.Unknown, presence.Of(new PinnedApp(exe, exe, "GoneApp")));
        Assert.True(SpinWait.SpinUntil(() => shortcuts.IsIndexed, TimeSpan.FromSeconds(5)), "the index was never built");
        Assert.Equal(Tasks.Presence.Gone, presence.Of(new PinnedApp(exe, exe, "GoneApp")));
    }

    /// <summary>Measured: not installed is ERROR_SUCCESS with no packages; a bad name is an error.</summary>
    [Fact]
    public void Windows_Says_Not_Installed_Only_When_It_Means_It()
    {
        InstalledPackages windows = new();

        Assert.False(windows.AnyInstalled($"Umbra.Never{Guid.NewGuid().ToString("N")[..8]}_8wekyb3d8bbwe"));
        Assert.Null(windows.AnyInstalled("not a family name!!"));
    }

    // ------------------------------------------------------------------ confirming

    private static Func<Presence> Always(Presence answer, Action? looked = null) => () =>
    {
        looked?.Invoke();
        return answer;
    };

    [Fact]
    public void One_Look_Never_Removes_A_Pin()
    {
        DeadPinWatch watch = new();

        Assert.False(watch.Look("app", 0, Always(Tasks.Presence.Gone)));
    }

    [Fact]
    public void Gone_Twice_A_Minute_Apart_Removes_It()
    {
        DeadPinWatch watch = new();

        Assert.False(watch.Look("app", 0, Always(Tasks.Presence.Gone)));
        Assert.False(watch.Look("app", Minute - 1, Always(Tasks.Presence.Gone)));
        Assert.True(watch.Look("app", Minute, Always(Tasks.Presence.Gone)));
    }

    [Fact]
    public void Anything_But_Gone_At_The_Second_Look_Starts_Over()
    {
        foreach (Presence answer in new[] { Tasks.Presence.Present, Tasks.Presence.Unknown })
        {
            DeadPinWatch watch = new();

            watch.Look("app", 0, Always(Tasks.Presence.Gone));
            Assert.False(watch.Look("app", Minute, Always(answer)));

            // A fresh minute from the next Gone, not from the first one.
            Assert.False(watch.Look("app", 2 * Minute, Always(Tasks.Presence.Gone)));
            Assert.False(watch.Look("app", 2 * Minute + Minute - 1, Always(Tasks.Presence.Gone)));
            Assert.True(watch.Look("app", 3 * Minute, Always(Tasks.Presence.Gone)));
        }
    }

    [Fact]
    public void Running_In_Between_Starts_Over()
    {
        DeadPinWatch watch = new();

        watch.Look("app", 0, Always(Tasks.Presence.Gone));
        watch.Running("app", 30_000);

        Assert.False(watch.Look("app", Minute, Always(Tasks.Presence.Gone)));
        Assert.True(watch.Look("app", 2 * Minute, Always(Tasks.Presence.Gone)));
    }

    /// <summary>The model asks on every window event; a healthy pin is looked at twice a minute at most.</summary>
    [Fact]
    public void A_Present_Pin_Is_Looked_At_No_More_Than_Every_Thirty_Seconds()
    {
        DeadPinWatch watch = new();
        int looks = 0;

        for (long now = 0; now < Minute; now += 1_000)
            watch.Look("app", now, Always(Tasks.Presence.Present, () => looks++));

        Assert.Equal(2, looks);
    }

    [Fact]
    public void The_Second_Look_Is_Scheduled_A_Minute_After_The_First()
    {
        DeadPinWatch watch = new();
        Assert.Null(watch.NextLookIn(0));

        watch.Look("app", 10_000, Always(Tasks.Presence.Gone));

        Assert.Equal(Minute - 5_000, watch.NextLookIn(15_000));
    }

    /// <summary>A look asked for inside the interval is owed at the interval's end, and said once.</summary>
    [Fact]
    public void A_Look_Asked_For_Too_Soon_Is_Owed_When_It_Falls_Due()
    {
        DeadPinWatch watch = new();
        watch.Look("app", 0, Always(Tasks.Presence.Present));
        Assert.Null(watch.NextLookIn(0));

        watch.Look("app", 10_000, Always(Tasks.Presence.Present));

        Assert.Equal(20_000, watch.NextLookIn(10_000));
        Assert.Null(watch.NextLookIn(10_000));
    }

    [Fact]
    public void A_Key_No_Longer_Pinned_Is_Forgotten()
    {
        DeadPinWatch watch = new();
        watch.Look("app", 0, Always(Tasks.Presence.Gone));

        watch.Keep([]);

        Assert.Null(watch.NextLookIn(0));
    }

    // ------------------------------------------------------------------ the model removes the pin

    private PinSettings Pins(params (string Key, string Path, string Name)[] pins)
    {
        File.WriteAllLines(_pinFile,
            ["# Pinned applications, in bar order. key|path|name", .. pins.Select(p => $"{p.Key}|{p.Path}|{p.Name}")]);
        return new PinSettings(_pinFile);
    }

    private static CollectionView NoWindows() => new(new List<ApplicationWindow>());

    private TaskbarModel Model(PinSettings pins, Func<long> clock) =>
        new(NoWindows(), pins, new PackagedAppNames(_ => null), new PackagedExecutable(new Families()), Presence(), clock);

    [Fact]
    public void The_Model_Removes_An_Uninstalled_Pin_After_A_Minute_And_Keeps_The_Rest()
    {
        string gone = Missing(@"Programs\GoneApp\GoneApp.exe");
        string uwp = $"{GoneFamily}!App";
        long now = 0;
        PinSettings pins = Pins((Notepad, Notepad, "Notepad"), (gone, gone, "GoneApp"), (uwp, PinnedApp.AppsFolderPrefix + uwp, "Gone UWP"));

        using TaskbarModel model = Model(pins, () => now);
        Assert.Equal(3, model.Slots.Count);

        now = Minute / 2;
        model.Recheck();
        Assert.Equal(3, model.Slots.Count);

        now = Minute;
        model.Recheck();

        Assert.Equal([Notepad], model.Slots.Select(s => s.Key));
        Assert.Equal([Notepad], pins.Pins.Select(p => p.Key));
        Assert.Equal([Notepad], new PinSettings(_pinFile).Pins.Select(p => p.Key));
    }

    [Fact]
    public void The_Model_Keeps_Pins_It_Cannot_Judge()
    {
        HashSet<char> used = [.. DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0]))];
        char letter = "ZYXWVUTSRQPONMLKJIHG".First(c => !used.Contains(c));
        string away = $@"{letter}:\Portable\Tool\Tool.exe";
        string live = StoreExe(LiveFamily, "1.0.0.0");
        long now = 0;
        PinSettings pins = Pins((away, away, "Tool"), (live, live, "Live"));

        using TaskbarModel model = Model(pins, () => now);
        for (now = 0; now <= 5 * Minute; now += Minute / 2) model.Recheck();

        Assert.Equal(2, pins.Pins.Count);
        Assert.False(model.RecheckScheduled);
    }

    /// <summary>On an idle desktop nothing rebuilds the row, so the second look has a timer of its own.</summary>
    [Fact]
    public void A_Suspected_Pin_Schedules_Its_Own_Second_Look()
    {
        string gone = Missing(@"Programs\GoneApp\GoneApp.exe");
        long now = 0;
        PinSettings pins = Pins((Notepad, Notepad, "Notepad"), (gone, gone, "GoneApp"));

        using TaskbarModel model = Model(pins, () => now);
        Assert.True(model.RecheckScheduled);

        // Past the due time, not on it: a tick a clock tick early found the look not yet due, and
        // live it then waited for a window event (+65 s for a look due at +30 s).
        Assert.True(model.RecheckInterval > TimeSpan.FromMilliseconds(Minute), $"scheduled for {model.RecheckInterval}");

        now = Minute;
        model.Recheck();

        Assert.False(model.RecheckScheduled);
    }

    /// <summary>
    /// Uninstalled while the bar runs. The uninstaller's window closing rebuilds the row inside the
    /// 30 s interval since the last look, so that rebuild cannot look; it leaves a timer for when
    /// it can. Live, without this, the pin was still there 150 s after its executable went.
    /// </summary>
    [Fact]
    public void An_Application_Uninstalled_While_The_Bar_Runs_Is_Removed()
    {
        string exe = Touch(@"Programs\Tool\Tool.exe");
        long now = 0;
        PinSettings pins = Pins((Notepad, Notepad, "Notepad"), (exe, exe, "Tool"));

        using TaskbarModel model = Model(pins, () => now);
        Assert.False(model.RecheckScheduled);

        File.Delete(exe);
        now = 10_000;
        model.Recheck();
        Assert.True(model.RecheckScheduled, "the rebuild inside the interval left nothing to look again");

        now = 30_500;
        model.Recheck();
        now = 30_500 + Minute;
        model.Recheck();

        Assert.Equal([Notepad], pins.Pins.Select(p => p.Key));
    }

    /// <summary>
    /// At startup the Start Menu is not read yet, so an ordinary pin's first look is unknown. It is
    /// looked at again on a timer, not at whatever window event comes next.
    /// </summary>
    [Fact]
    public void A_Pin_First_Looked_At_Before_The_Start_Menu_Was_Read_Is_Looked_At_Again()
    {
        string gone = Missing(@"Programs\GoneApp\GoneApp.exe");
        long now = 0;
        PinSettings pins = Pins((gone, gone, "GoneApp"));
        ShortcutResolver unread = new(_startMenu);
        PinPresence presence = new(new PackagedExecutable(new Families()), unread);

        using TaskbarModel model = new(NoWindows(), pins, new PackagedAppNames(_ => null),
            new PackagedExecutable(new Families()), presence, () => now);

        Assert.True(model.RecheckScheduled, "nothing will look again until some window opens");
        Assert.True(SpinWait.SpinUntil(() => unread.IsIndexed, TimeSpan.FromSeconds(5)), "the index was never built");

        now = Minute / 2;
        model.Recheck();
        now = Minute / 2 + Minute;
        model.Recheck();

        Assert.Empty(pins.Pins);
    }
}
