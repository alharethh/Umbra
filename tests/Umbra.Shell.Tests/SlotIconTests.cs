using System.IO;
using Umbra.Shell.Shell;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// Which slots are allowed to synthesise an icon, and why widening it froze the whole bar.
/// </summary>
/// <remarks>
/// <para>
/// A pinned application is the one thing on the row that can exist with no window to take an icon
/// from, so it — and only it — gets one read out of its executable. Extending that to any slot
/// lacking a live icon looks harmless and is not.
/// </para>
/// <para>
/// <b><c>GetImageFromAssociatedIcon</c> is a synchronous shell call made on the UI thread, and it
/// can never return.</b> On the development machine it hangs indefinitely for
/// <c>C:\Program Files\Ascent\Ascent.exe</c>. The bar froze before its window was ever shown: no
/// icons, no clock, no Start button, no error and nothing in the log, because nothing had thrown.
/// It reproduced only with the tray switched off, which is pure timing — the tray's extra startup
/// work delays the first layout long enough for the live icon to arrive, so the fallback is never
/// reached. That made it look like a tray bug for an hour.
/// </para>
/// <para>
/// <b>These tests need a shortcut that actually resolves.</b> The first version of this file used
/// an empty Start Menu, so the resolver found nothing and the slot returned null whether the guard
/// was there or not — it passed against a build with the guard deleted. A real <c>.lnk</c> is what
/// makes the widened version observably produce an icon.
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
    /// and these tests would then pass because nothing resolved rather than because the guard
    /// held. That is the trap the first two versions of this file fell into.
    /// </remarks>
    private static TaskbarSlot Slot(PinnedApp? pin, ShortcutResolver resolver) =>
        new("whatever", pin, resolver);

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
    /// An unpinned slot with no window never reaches the shell.
    /// </summary>
    /// <remarks>
    /// The shortcut above resolves to a real executable with a perfectly good icon, so a build that
    /// asks the shell here returns one and fails this. That is the point: the assertion is "null",
    /// but what it guards is "did not go looking".
    /// </remarks>
    [Fact]
    public void AnUnpinnedSlotDoesNotSynthesiseAnIcon()
    {
        TaskbarSlot slot = Slot(pin: null, Resolver());

        Assert.False(slot.IsPinned);
        Assert.Null(slot.Icon);
    }

    /// <summary>The same slot, pinned, is allowed to and does.</summary>
    /// <remarks>
    /// The other half of the pair. Without it the guard could be "never synthesise anything", which
    /// would pass the test above and quietly cost every pinned application its icon.
    /// </remarks>
    [Fact]
    public void APinnedSlotDoesSynthesiseOne()
    {
        PinnedApp pin = new(@"C:\Program Files\Whatever\whatever.exe", @"C:\nowhere\gone.exe", "whatever");
        TaskbarSlot slot = Slot(pin, Resolver());

        Assert.True(slot.IsPinned);
        Assert.NotNull(slot.Icon);
    }

    /// <summary>And it must answer immediately when it is not going to look.</summary>
    [Fact]
    public void AnUnpinnedSlotAnswersImmediately()
    {
        TaskbarSlot slot = Slot(pin: null, Resolver());

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        _ = slot.Icon;
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 200,
            $"an unpinned slot took {watch.ElapsedMilliseconds} ms to report no icon, so it went to the shell");
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
        TaskbarSlot slot = Slot(pin: null, Resolver());

        Assert.NotNull(slot.LaunchTarget);
        Assert.Equal(Target, slot.LaunchTarget!.Value.Path, ignoreCase: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_startMenu)) Directory.Delete(_startMenu, recursive: true);
        GC.SuppressFinalize(this);
    }
}
