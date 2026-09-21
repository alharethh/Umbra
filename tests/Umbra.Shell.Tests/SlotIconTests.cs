using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Shell.Shell;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// What a slot is allowed to do to find its icon, and what it must never do on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file used to guard a different invariant, and that invariant is deliberately gone.</b>
/// It asserted that only a <i>pinned</i> slot would ever synthesise an icon, because
/// <c>GetImageFromAssociatedIcon</c> was called straight from the <c>Icon</c> getter and never
/// returns for <c>C:\Program Files\Ascent\Ascent.exe</c> on the development machine — the bar
/// froze before its window was ever shown. Narrowing which slots could reach a blocking call was
/// a way of making the freeze rarer, not of fixing it, and it cost every unpinned application
/// with no window icon its artwork. Measured, that is most of them: seven of ten visible windows
/// return nothing from <c>WM_GETICON</c>.
/// </para>
/// <para>
/// The invariant now is the stronger one: <b>the getter never touches the shell at all</b>. So
/// these tests assert that it answers immediately however badly the shell behaves, and that the
/// icon arrives afterwards on its own.
/// </para>
/// <para>
/// <b>These tests need a shortcut that actually resolves.</b> The first version of the old file
/// used an empty Start Menu, so nothing resolved and it passed against a build with the guard
/// deleted. A real <c>.lnk</c> to a real executable is what makes the assertions discriminate.
/// </para>
/// </remarks>
public class SlotIconTests : IDisposable
{
    private readonly string _startMenu =
        Path.Combine(Path.GetTempPath(), $"umbra-start-menu-{Guid.NewGuid():N}");

    /// <summary>A real executable every Windows install has, whose icon extracts quickly.</summary>
    private static readonly string Target =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    public SlotIconTests()
    {
        Directory.CreateDirectory(_startMenu);
        CreateShortcut(Path.Combine(_startMenu, "whatever.lnk"), Target);
    }

    private static void CreateShortcut(string path, string target)
    {
        object link = ShellLinkInterop.Create()
            ?? throw new InvalidOperationException("no ShellLink");

        ((ShellLinkInterop.IShellLinkW)link).SetPath(target);
        ((ShellLinkInterop.IPersistFile)link).Save(path, true);
    }

    private ShortcutResolver Resolver() => new(_startMenu);

    /// <remarks>
    /// Keyed "whatever", not a path. An unpinned slot with no windows has no executable path, so
    /// the only name it can offer the resolver is its own key — exactly the case of an
    /// AUMID-keyed application. Keying it with a path would leave the resolver unable to match,
    /// and these tests would then pass because nothing resolved rather than because the behaviour
    /// held. That is the trap the first two versions of the old file fell into.
    /// </remarks>
    private TaskbarSlot Slot(PinnedApp? pin, AppIconResolver? icons = null) =>
        new("whatever", pin, Resolver(), icons);

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

    /// <summary>The shortcut really does resolve — without this the rest proves nothing.</summary>
    [Fact]
    public void ThePreconditionHolds()
    {
        Assert.True(File.Exists(Target), "notepad.exe is missing, so these tests cannot discriminate");

        AppShortcut? found = Resolver().Resolve("whatever", null);

        Assert.NotNull(found);
        Assert.Equal(Target, found.Target, ignoreCase: true);
    }

    /// <summary>
    /// The icon arrives, from the Start Menu, for a slot that is <b>not</b> pinned.
    /// </summary>
    /// <remarks>
    /// The case the old guard broke. This slot has no pin and no windows, so under the previous
    /// rule it drew nothing for as long as it was on the bar.
    /// </remarks>
    [Fact]
    public void AnUnpinnedSlotStillGetsItsApplicationIcon()
    {
        TaskbarSlot slot = Slot(pin: null);

        Assert.False(slot.IsPinned);

        // First read kicks off resolution and returns whatever is known so far, which is nothing.
        _ = slot.Icon;
        Assert.True(Eventually(() => slot.Icon is not null));
    }

    /// <summary>And a pinned one does too, from its recorded path.</summary>
    [Fact]
    public void APinnedSlotGetsItsIconFromTheRecordedPath()
    {
        PinnedApp pin = new(Target, Target, "whatever");
        TaskbarSlot slot = Slot(pin);

        _ = slot.Icon;
        Assert.True(Eventually(() => slot.Icon is not null));
    }

    /// <summary>
    /// The getter answers immediately even when every extraction hangs forever.
    /// </summary>
    /// <remarks>
    /// <b>This is the freeze, reproduced.</b> Put a blocking call back into the <c>Icon</c> getter
    /// and this test stops returning rather than failing — which is precisely what the bar did.
    /// </remarks>
    [Fact]
    public void TheIconGetterAnswersImmediatelyWhileTheShellIsWedged()
    {
        using ManualResetEventSlim release = new(false);
        AppIconResolver icons = new(
            new BlockingSource(release), timeout: TimeSpan.FromMilliseconds(200));

        TaskbarSlot slot = Slot(pin: null, icons);

        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) _ = slot.Icon;
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 200,
            $"fifty reads took {watch.ElapsedMilliseconds} ms, so the getter is reaching the shell");

        release.Set();
    }

    /// <summary>
    /// A slot whose icon genuinely cannot be found draws a placeholder rather than a gap.
    /// </summary>
    /// <remarks>
    /// An empty slot reads as a layout fault — that is how the stale Discord pin was reported, as
    /// "a weird logo to the right of firefox". A placeholder is visibly a placeholder.
    /// </remarks>
    [Fact]
    public void AnUnresolvableSlotFallsBackToThePlaceholder()
    {
        AppIconResolver icons = new(new EmptySource(), maxAttempts: 1);
        TaskbarSlot slot = Slot(pin: null, icons);

        Assert.True(Eventually(() =>
        {
            _ = slot.Icon;
            return icons.Exhausted(slot.Key);
        }));

        Assert.Same(Umbra.Shell.Rendering.PlaceholderIcon.Get(), slot.Icon);
    }

    /// <summary>
    /// But not before it has given up — a row of placeholders on every start would look broken.
    /// </summary>
    [Fact]
    public void ThePlaceholderDoesNotAppearWhileResolutionIsStillPlausible()
    {
        using ManualResetEventSlim release = new(false);
        AppIconResolver icons = new(new BlockingSource(release), timeout: TimeSpan.FromSeconds(30));

        TaskbarSlot slot = Slot(pin: null, icons);

        Assert.Null(slot.Icon);
        Assert.False(icons.Exhausted(slot.Key));

        release.Set();
    }

    /// <summary>
    /// Narrowing the icon path must not narrow the launch path.
    /// </summary>
    /// <remarks>
    /// A click has to resolve a target even for a slot that draws nothing, and unlike layout it is
    /// allowed to take a moment.
    /// </remarks>
    [Fact]
    public void TheLaunchPathStillResolvesForAnUnpinnedSlot()
    {
        TaskbarSlot slot = Slot(pin: null);

        Assert.NotNull(slot.LaunchTarget);
        Assert.Equal(Target, slot.LaunchTarget!.Value.Path, ignoreCase: true);
    }

    private sealed class BlockingSource(ManualResetEventSlim release) : IIconSource
    {
        public ImageSource? Extract(IconCandidate candidate)
        {
            release.Wait();
            return null;
        }
    }

    private sealed class EmptySource : IIconSource
    {
        public ImageSource? Extract(IconCandidate candidate) => null;
    }

    /// <remarks>
    /// Retried, because several of these tests deliberately leave a resolution in flight and that
    /// worker may still have the <c>.lnk</c> open when the test method returns. Failing teardown
    /// on that race would make the suite flaky for a reason unrelated to anything it asserts.
    /// </remarks>
    public void Dispose()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(_startMenu)) Directory.Delete(_startMenu, recursive: true);
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }

        GC.SuppressFinalize(this);
    }
}
