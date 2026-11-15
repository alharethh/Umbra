using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// A packaged application's icon comes from its package, not from its executable.
/// </summary>
/// <remarks>
/// Reported on r/OLED_Gaming: WhatsApp showed the default application icon. Reproduced on the
/// laptop, 2026-11-14: <c>WhatsApp.Root.exe</c> contains no icons, its window carries no app ID,
/// and asking the shell for the executable's icon answered with Windows' generic program icon as
/// a success. Its process and its manifest both name the app ID, and the Apps folder item for that
/// ID draws the real logo.
/// </remarks>
public sealed class PackagedAppIconTests : IDisposable
{
    private const string FullName = "Fake.Chat_2.2644.101.0_x64__abcdefghjkmnp";
    private const string Family = "Fake.Chat_abcdefghjkmnp";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"umbra-package-{Guid.NewGuid():N}");
    private readonly string _package;

    public PackagedAppIconTests()
    {
        _package = Path.Combine(_root, "WindowsApps", FullName);
        Directory.CreateDirectory(_package);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Manifest(params (string Id, string Exe)[] apps) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="Fake.Chat" Publisher="CN=Somebody" Version="2.2644.101.0" ProcessorArchitecture="x64" />
          <Applications>
            {string.Concat(apps.Select(a => $"<Application Id=\"{a.Id}\" Executable=\"{a.Exe}\" EntryPoint=\"Windows.FullTrustApplication\" />"))}
          </Applications>
        </Package>
        """;

    private string Executable(string relative, string manifest)
    {
        File.WriteAllText(Path.Combine(_package, "AppxManifest.xml"), manifest);

        string path = Path.Combine(_package, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    // ------------------------------------------------------------------ package names and IDs

    [Theory]
    [InlineData("5319275A.WhatsAppDesktop_2.2644.101.0_x64__cv1g1gvanyjgm", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm")]
    [InlineData("Claude_4.29.0.0_x64__pzs8sxrjxfjjc", "Claude_pzs8sxrjxfjjc")]
    [InlineData("Discord", null)]
    [InlineData("app-1.0.9258", null)]
    public void The_Family_Name_Is_The_Package_Name_And_Publisher(string folder, string? family) =>
        Assert.Equal(family, AppIdentity.FamilyNameOf(folder));

    [Fact]
    public void The_Manifest_Names_The_App_That_Runs_The_Executable() =>
        Assert.Equal($"{Family}!Main",
            PackagedExecutable.AppIdFrom(Manifest(("Helper", "bin/helper.exe"), ("Main", "Chat.Root.exe")), Family, "chat.root.exe"));

    [Fact]
    public void A_Nested_Executable_Is_Matched_With_Either_Slash() =>
        Assert.Equal($"{Family}!App",
            PackagedExecutable.AppIdFrom(Manifest(("Other", "x.exe"), ("App", "app/Chat.exe")), Family, @"app\Chat.exe"));

    /// <summary>A launcher can start a helper the manifest does not list; with one app it is that app's.</summary>
    [Fact]
    public void An_Unlisted_Executable_Belongs_To_The_Only_App() =>
        Assert.Equal($"{Family}!App", PackagedExecutable.AppIdFrom(Manifest(("App", "Launcher.exe")), Family, "Chat.exe"));

    [Fact]
    public void With_Several_Apps_An_Unlisted_Executable_Is_Not_Guessed() =>
        Assert.Null(PackagedExecutable.AppIdFrom(Manifest(("A", "a.exe"), ("B", "b.exe")), Family, "c.exe"));

    [Fact]
    public void A_Closed_Pins_Path_Is_Enough_To_Find_Its_App_Id()
    {
        string exe = Executable(@"app\Chat.Root.exe", Manifest(("App", @"app\Chat.Root.exe")));

        Assert.Equal($"{Family}!App", new PackagedExecutable(new NoPackagesListed()).AppIdFor(exe));
    }

    [Fact]
    public void An_Ordinary_Executable_Has_No_App_Id() =>
        Assert.Null(new PackagedExecutable(new NoPackagesListed()).AppIdFor(Path.Combine(Environment.SystemDirectory, "notepad.exe")));

    [Theory]
    [InlineData("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", true)]
    [InlineData("Microsoft.Windows.Explorer", false)]
    [InlineData(@"C:\Apps\x.exe", false)]
    [InlineData(null, false)]
    public void Only_A_Family_Bang_App_Id_Counts_As_Packaged(string? id, bool packaged) =>
        Assert.Equal(packaged, TaskbarSlot.IsPackagedAppId(id));

    // ------------------------------------------------------------------ the slot asks the package first

    /// <summary>
    /// The fix itself: a pinned packaged application that is not running asks for its package's
    /// logo before its executable, whose "icon" may be Windows' generic one.
    /// </summary>
    [Fact]
    public void A_Closed_Packaged_Pin_Asks_For_Its_Package_Logo_Before_Its_Executable()
    {
        string exe = Executable("Chat.Root.exe", Manifest(("App", "Chat.Root.exe")));
        RecordingSource source = new();
        TaskbarSlot slot = new(exe, new PinnedApp(exe, exe, "Chat"), new ShortcutResolver(_root), new AppIconResolver(source), packages: new PackagedExecutable(new NoPackagesListed()));

        _ = slot.Icon;

        Assert.True(Eventually(() => source.Asked.Count >= 2), $"asked: {string.Join(" | ", source.Asked)}");
        Assert.Equal([$@"shell:AppsFolder\{Family}!App", exe], source.Asked.Take(2));
    }

    [Fact]
    public void An_Unpackaged_Pin_Asks_Only_For_Its_Executable()
    {
        string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        RecordingSource source = new();
        TaskbarSlot slot = new(exe, new PinnedApp(exe, exe, "Notepad"), new ShortcutResolver(_root), new AppIconResolver(source), packages: new PackagedExecutable(new NoPackagesListed()));

        _ = slot.Icon;

        Assert.True(Eventually(() => source.Asked.Count >= 1));
        Assert.DoesNotContain(source.Asked, a => a.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The real extraction, against Settings, which every Windows 11 install has. It must keep its
    /// transparency: copied out naively the bitmap's alpha is lost and the logo sits on black.
    /// </summary>
    [Fact]
    public void A_Packaged_Apps_Logo_Is_Read_From_The_Apps_Folder_With_Its_Transparency()
    {
        ImageSource? image = new ShellIconSource().Extract(
            ShellIconSource.ForAppId("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"));

        var bitmap = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(image);
        Assert.Equal(ShellIconSource.Extent, bitmap.PixelWidth);

        // Converted first: the raw bytes of a format with no alpha still have a fourth byte, and
        // reading that would pass a bitmap that WPF draws as opaque.
        var straight = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[straight.PixelWidth * straight.PixelHeight * 4];
        straight.CopyPixels(pixels, straight.PixelWidth * 4, 0);

        int transparent = Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] == 0);
        int opaque = Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] == 255);

        Assert.True(transparent > 0, "no transparent pixel: the alpha was lost");
        Assert.True(opaque > 0, "no opaque pixel: nothing was drawn");
    }

    // ------------------------------------------------------------------ the pin's name

    [Fact]
    public void A_Packaged_Desktop_Apps_Pin_Takes_Its_Apps_Folder_Name()
    {
        PinnedApp pin = PinSettings.RecordFor(@"C:\x\WhatsApp.Root.exe", @"C:\x\WhatsApp.Root.exe", null, "WhatsApp", null, "WhatsApp");
        Assert.Equal("WhatsApp", pin.Name);
    }

    [Fact]
    public void Without_A_Process_Name_The_Pin_Is_Named_As_Before()
    {
        string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.Equal(PinSettings.RecordFor(exe, exe, "Notepad", "x", null).Name, PinSettings.RecordFor(exe, exe, "Notepad", "x", null, null).Name);
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

    /// <summary>Windows listing no installed versions: only the folder a path names counts.</summary>
    private sealed class NoPackagesListed : IPackageFolders
    {
        public IEnumerable<string> FoldersOf(string familyName) => [];
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
