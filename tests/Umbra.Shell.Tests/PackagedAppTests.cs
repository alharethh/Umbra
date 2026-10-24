using Umbra.Shell.Tasks;
using Xunit;

namespace Umbra.Shell.Tests;

/// <summary>
/// UWP applications are drawn inside <c>ApplicationFrameHost.exe</c>, so their executable names and
/// draws every one of them as the host. Figures are what ManagedShell's <c>ApplicationWindow</c>
/// reported for Settings and Calculator on the laptop, 2026-10-20.
/// </summary>
public sealed class PackagedAppTests
{
    private const string FrameHost = @"C:\Windows\System32\ApplicationFrameHost.exe";
    private const string SettingsId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    [Fact]
    public void A_Frame_Hosted_Window_Is_Not_Named_After_The_Host()
    {
        string? name = TaskbarSlot.NameFor(FrameHost, "Application Frame Host", SettingsId, "Settings", null);

        Assert.Equal("Settings", name);
    }

    [Fact]
    public void The_Packaged_Name_Beats_The_Title_Once_Resolved()
    {
        // A title is per-document; the packaged name is the application's.
        string? name = TaskbarSlot.NameFor(FrameHost, "Application Frame Host", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App", "IMG_0001.jpg - Photos", "Photos");

        Assert.Equal("Photos", name);
    }

    [Fact]
    public void An_Ordinary_Window_Is_Still_Named_By_Its_File_Description()
    {
        Assert.Equal("Firefox", TaskbarSlot.NameFor(@"C:\Program Files\Mozilla Firefox\firefox.exe", "Firefox", "308046B0AF4A39CB", "Some page — Mozilla Firefox", null));
        Assert.Equal("tool", TaskbarSlot.NameFor(@"C:\x\tool.exe", null, null, "Title", null));
    }

    // Windows Terminal on the laptop, 2026-10-23: no ID on its window, so the slot is keyed by the
    // path; the process answers GetApplicationUserModelId with the package's ID.
    private const string TerminalExe = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24.12741.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe";
    private const string TerminalId = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";

    [Fact]
    public void A_Packaged_Desktop_Application_Is_Named_As_Start_Names_It()
    {
        // Its file description is the host process's, not the application's.
        Assert.Equal("Terminal", TaskbarSlot.NameFor(TerminalExe, "Windows Terminal Host", null, null, "Terminal"));

        // Until the Apps folder answers, the description stands in rather than the bare path.
        Assert.Equal("Windows Terminal Host", TaskbarSlot.NameFor(TerminalExe, "Windows Terminal Host", null, null, null));
    }

    [Fact]
    public void A_Path_Keyed_Packaged_Application_Is_Activated_By_Its_Process_Id()
    {
        // The path is what the bar used to hand ActivateApplication, and nothing started.
        Assert.Equal(TerminalId, TaskbarSlot.ActivationIdFor(TerminalExe, TerminalId));

        // Everything else keeps its key: a UWP frame's or an AUMID-keyed window's is already its ID.
        Assert.Equal(SettingsId, TaskbarSlot.ActivationIdFor(SettingsId, null));
        Assert.Equal(@"C:\x\tool.exe", TaskbarSlot.ActivationIdFor(@"C:\x\tool.exe", null));
    }

    [Fact]
    public void An_Unpackaged_Process_Has_No_Process_Id()
    {
        // The test host is an ordinary process, so this is the answer every unpackaged
        // application gets: null, and its key is left alone.
        Assert.Null(ProcessAppId.ForProcess((uint)Environment.ProcessId));
        Assert.Null(ProcessAppId.For(IntPtr.Zero));
    }

    [Fact]
    public void A_Packaged_Process_Answers_With_Its_Id()
    {
        // The real call, against whichever packaged process is running. Skipped in effect when
        // none is, which a CI machine may be; the laptop always has one (Start's own host).
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName("StartMenuExperienceHost"))
        {
            string? id = ProcessAppId.ForProcess((uint)process.Id);

            Assert.NotNull(id);
            Assert.Contains('!', id);
            return;
        }
    }

    [Fact]
    public void The_Frame_Host_Is_Never_An_Icon_Source()
    {
        // Precondition: the host really is on disk, so the refusal is not just a missing file.
        Assert.True(System.IO.File.Exists(FrameHost));

        Assert.False(TaskbarSlot.UsableIconSource(FrameHost));
        Assert.True(TaskbarSlot.UsableIconSource(@"C:\Windows\notepad.exe"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\ApplicationFrameHost.exe", true)]
    [InlineData(@"c:\windows\system32\applicationframehost.exe", true)]
    [InlineData(@"C:\Windows\explorer.exe", false)]
    [InlineData(SettingsId, false)]
    [InlineData(null, false)]
    public void Recognises_The_Frame_Host(string? path, bool expected) =>
        Assert.Equal(expected, PackagedAppNames.IsFrameHost(path));

    [Fact]
    public void Names_Resolve_Off_The_Calling_Thread_And_Only_Once()
    {
        int calls = 0;
        int callerThread = Environment.CurrentManagedThreadId;
        int? resolverThread = null;
        using ManualResetEventSlim done = new();

        PackagedAppNames names = new(id =>
        {
            Interlocked.Increment(ref calls);
            resolverThread = Environment.CurrentManagedThreadId;
            return "Settings";
        });

        Assert.Null(names.Lookup(SettingsId));

        names.Request(SettingsId, done.Set);
        names.Request(SettingsId, () => { });

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("Settings", names.Lookup(SettingsId));
        Assert.NotEqual(callerThread, resolverThread);

        names.Request(SettingsId, () => { });
        Thread.Sleep(50);
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// With a bar on every monitor the shared instance is asked for the same ID by one slot per
    /// bar. Only the first caller used to be told, so the second bar's Settings stayed named after
    /// its window title, or the frame host, until something else raised its name.
    /// </summary>
    [Fact]
    public void Every_Caller_Waiting_On_A_Name_Is_Told_When_It_Arrives()
    {
        using ManualResetEventSlim release = new();
        int calls = 0;

        PackagedAppNames names = new(id =>
        {
            Interlocked.Increment(ref calls);
            release.Wait(TimeSpan.FromSeconds(5));
            return "Settings";
        });

        using ManualResetEventSlim firstBar = new();
        using ManualResetEventSlim secondBar = new();
        using ManualResetEventSlim secondModel = new();

        // All three ask before the worker answers, as the bars' first layout passes do.
        names.Request(SettingsId, firstBar.Set);
        names.Request(SettingsId, secondBar.Set);
        names.Request(SettingsId.ToUpperInvariant(), secondModel.Set);

        release.Set();

        Assert.True(firstBar.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(secondBar.Wait(TimeSpan.FromSeconds(5)), "the second caller was never told the name arrived");
        Assert.True(secondModel.Wait(TimeSpan.FromSeconds(5)), "a caller spelling the ID in another case was never told");
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// A slot asks from its <c>DisplayName</c> getter and the model from every rebuild, so one
    /// caller asks many times while the worker runs. Appending each ask told it once per ask when
    /// the name arrived, and while a shell call never returned the list grew by one delegate per
    /// read for the rest of the session, each keeping its slot alive.
    /// </summary>
    [Fact]
    public void One_Caller_Asking_Many_Times_Is_Told_Once()
    {
        using ManualResetEventSlim release = new();
        PackagedAppNames names = new(id =>
        {
            release.Wait(TimeSpan.FromSeconds(5));
            return "Settings";
        });

        Caller firstSlot = new();
        Caller secondSlot = new();
        using ManualResetEventSlim last = new();

        for (int i = 0; i < 100; i++)
        {
            names.Request(SettingsId, firstSlot.OnResolved);
            names.Request(SettingsId, secondSlot.OnResolved);
        }

        // Callers are told in the order they joined, on one worker, so once this one has been
        // told every earlier one has been too.
        names.Request(SettingsId, last.Set);
        release.Set();

        Assert.True(last.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, firstSlot.Told);
        Assert.Equal(1, secondSlot.Told);
    }

    private sealed class Caller
    {
        private int _told;

        public int Told => Volatile.Read(ref _told);

        public void OnResolved() => Interlocked.Increment(ref _told);
    }

    [Fact]
    public void A_Caller_After_The_Name_Arrived_Reads_It_Instead_Of_Being_Called()
    {
        PackagedAppNames names = new(_ => "Settings");
        using ManualResetEventSlim done = new();

        names.Request(SettingsId, done.Set);
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));

        bool late = false;
        names.Request(SettingsId, () => late = true);
        Thread.Sleep(50);

        Assert.False(late);
        Assert.Equal("Settings", names.Lookup(SettingsId));
    }

    [Fact]
    public void A_Name_That_Was_Not_Found_Is_Not_Asked_For_Again_And_Calls_Nobody()
    {
        using ManualResetEventSlim answered = new();
        int calls = 0;

        PackagedAppNames names = new(_ =>
        {
            Interlocked.Increment(ref calls);
            answered.Set();
            return null;
        });

        bool told = false;
        names.Request(SettingsId, () => told = true);
        Assert.True(answered.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);

        names.Request(SettingsId, () => told = true);
        Thread.Sleep(50);

        Assert.False(told);
        Assert.Equal(1, calls);
        Assert.Null(names.Lookup(SettingsId));
    }

    [Fact]
    public void The_Apps_Folder_Names_Settings()
    {
        // The real resolver, against a package every Windows 11 install has.
        PackagedAppNames names = new();
        using ManualResetEventSlim done = new();

        names.Request(SettingsId, done.Set);

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("Settings", names.Lookup(SettingsId));
    }
}
