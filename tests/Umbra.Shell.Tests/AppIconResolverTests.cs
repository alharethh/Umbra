using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// The four properties that make icon resolution safe, and the two bugs that needed each.
/// </summary>
/// <remarks>
/// <para>
/// <b>It must not block the caller</b>, because the caller is a property getter run by the layout
/// pass. <c>SHGetFileInfo</c> never returns for at least one executable on the development
/// machine, and calling it there froze the bar before its window was ever shown — black strip, no
/// clock, no Start button, nothing thrown and nothing logged.
/// </para>
/// <para>
/// <b>It must give up on a wedged path and never ask again</b>, because a timed-out extraction
/// strands its worker; there is no way to cancel a blocked cross-process call, so asking twice
/// costs two threads for nothing.
/// </para>
/// <para>
/// <b>It must retry an ordinary failure</b>, which is the fix for "sometimes Umbra doesn't find
/// icons". ManagedShell reads the executable once, keeps whatever it had if that fails, and never
/// looks again — so a single unlucky read against a cold shell cache lasted the whole session.
/// </para>
/// <para>
/// <b>It must build its candidate list off the calling thread</b>, because finding an
/// application's Start Menu shortcut enumerates every <c>.lnk</c> under both Start Menus. Doing
/// that on the first layout pass would be the original freeze wearing a different hat.
/// </para>
/// </remarks>
public class AppIconResolverTests
{
    private static ImageSource AnIcon()
    {
        BitmapSource icon = BitmapSource.Create(
            1, 1, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, new byte[4], 4);

        icon.Freeze();
        return icon;
    }

    /// <summary>A source whose behaviour each test dictates, and which records what it was asked.</summary>
    private sealed class FakeSource : IIconSource
    {
        private readonly List<IconCandidate> _asked = [];

        public Func<IconCandidate, ImageSource?> Behaviour { get; set; } = _ => null;

        public IReadOnlyList<IconCandidate> Asked
        {
            get { lock (_asked) return [.. _asked]; }
        }

        public ImageSource? Extract(IconCandidate candidate)
        {
            lock (_asked) _asked.Add(candidate);
            return Behaviour(candidate);
        }
    }

    private static readonly IconCandidate Good = new(@"C:\good.exe");
    private static readonly IconCandidate Wedged = new(@"C:\wedged.exe");
    private static readonly IconCandidate Bad = new(@"C:\bad.exe");

    private static Func<IReadOnlyList<IconCandidate>> Candidates(params IconCandidate[] items) =>
        () => items;

    /// <summary>Waits for a condition the worker will satisfy, without sleeping the whole budget.</summary>
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

    [Fact]
    public void AnIconIsResolvedAndThenServedFromTheCache()
    {
        FakeSource source = new() { Behaviour = _ => AnIcon() };
        AppIconResolver resolver = new(source);

        Assert.Null(resolver.Lookup("app"));
        Assert.True(resolver.Request("app", Candidates(Good), () => { }));
        Assert.True(Eventually(() => resolver.Lookup("app") is not null));

        // A second request is a no-op: the answer is known.
        Assert.False(resolver.Request("app", Candidates(Good), () => { }));
        Assert.Single(source.Asked);
    }

    /// <summary>
    /// The whole reason this class exists: a source that never returns must not hold up the caller.
    /// </summary>
    /// <remarks>
    /// Deleting the timeout in <c>Extract</c> does not fail this one — <c>Request</c> is
    /// asynchronous either way — which is exactly why
    /// <see cref="AWedgedCandidateIsAbandonedAndNeverAskedAgain"/> exists alongside it. This test
    /// guards the weaker property that the <i>call site</i> is safe; that one guards the recovery.
    /// </remarks>
    [Fact]
    public void RequestingDoesNotBlockOnASourceThatNeverReturns()
    {
        using ManualResetEventSlim release = new(false);
        FakeSource source = new() { Behaviour = _ => { release.Wait(); return null; } };
        AppIconResolver resolver = new(source, timeout: TimeSpan.FromMilliseconds(200));

        Stopwatch watch = Stopwatch.StartNew();
        resolver.Request("app", Candidates(Wedged), () => { });
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 100,
            $"Request took {watch.ElapsedMilliseconds} ms, so it waited on the source");

        release.Set();
    }

    /// <summary>
    /// A candidate that does not come back is abandoned, and no later slot pays for it again.
    /// </summary>
    /// <remarks>
    /// Removing the timeout hangs this test rather than failing it, which is the honest signal:
    /// without it the resolver waits forever too. Removing only the <c>_wedged</c> bookkeeping
    /// fails the final assertion.
    /// </remarks>
    [Fact]
    public void AWedgedCandidateIsAbandonedAndNeverAskedAgain()
    {
        using ManualResetEventSlim release = new(false);
        FakeSource source = new() { Behaviour = _ => { release.Wait(); return null; } };

        // One attempt each, so "gave up" is reachable and the second request cannot be mistaken
        // for one that is merely still retrying.
        AppIconResolver resolver = new(source, timeout: TimeSpan.FromMilliseconds(150), maxAttempts: 1);

        resolver.Request("first", Candidates(Wedged), () => { });
        Assert.True(Eventually(() => resolver.WedgedCount == 1));

        // A different application, the same bad file. It must not be tried a second time.
        resolver.Request("second", Candidates(Wedged), () => { });
        Assert.True(Eventually(() => resolver.Exhausted("second")));

        release.Set();
        Assert.Single(source.Asked);
    }

    /// <summary>
    /// An ordinary failure is transient until proven otherwise. This is the "sometimes" fix.
    /// </summary>
    /// <remarks>
    /// Make the resolver give up after one failure and this goes red: the second request returns
    /// false and the icon never arrives.
    /// </remarks>
    [Fact]
    public void AnOrdinaryFailureIsRetried()
    {
        int calls = 0;
        FakeSource source = new() { Behaviour = _ => ++calls >= 2 ? AnIcon() : null };
        AppIconResolver resolver = new(source, maxAttempts: 3);

        resolver.Request("app", Candidates(Good), () => { });
        Assert.True(Eventually(() => !resolver.Exhausted("app") && calls >= 1));
        Assert.True(Eventually(() => resolver.Lookup("app") is null && calls == 1));

        // The row rebuilds on every window event anywhere on the desktop; that is the retry.
        Assert.True(Eventually(() => resolver.Request("app", Candidates(Good), () => { })));
        Assert.True(Eventually(() => resolver.Lookup("app") is not null));
    }

    /// <summary>But not forever, or every window that opens anywhere costs a round of shell calls.</summary>
    [Fact]
    public void AfterEnoughFailuresItGivesUp()
    {
        FakeSource source = new() { Behaviour = _ => null };
        AppIconResolver resolver = new(source, maxAttempts: 2);

        for (int i = 0; i < 6 && !resolver.Exhausted("app"); i++)
        {
            resolver.Request("app", Candidates(Good), () => { });
            Thread.Sleep(20);
        }

        Assert.True(Eventually(() => resolver.Exhausted("app")));
        Assert.False(resolver.Request("app", Candidates(Good), () => { }));
        Assert.True(source.Asked.Count <= 2, $"asked {source.Asked.Count} times against a limit of 2");
    }

    /// <summary>Best first, and nothing after the first success is touched.</summary>
    [Fact]
    public void CandidatesAreTriedInOrderAndStopAtTheFirstThatWorks()
    {
        FakeSource source = new() { Behaviour = c => c.Path == Good.Path ? AnIcon() : null };
        AppIconResolver resolver = new(source);

        resolver.Request("app", Candidates(Bad, Good, new IconCandidate(@"C:\never.exe")), () => { });
        Assert.True(Eventually(() => resolver.Lookup("app") is not null));

        Assert.Equal([Bad.Path, Good.Path], source.Asked.Select(c => c.Path));
    }

    /// <summary>
    /// The candidate list is built on the worker, not by the caller.
    /// </summary>
    /// <remarks>
    /// This is the one that stops the Start Menu scan creeping back onto the UI thread. Evaluate
    /// the factory eagerly inside <c>Request</c> and it goes red.
    /// </remarks>
    [Fact]
    public void TheCandidateListIsBuiltOffTheCallingThread()
    {
        int caller = Environment.CurrentManagedThreadId;
        int builder = caller;

        FakeSource source = new() { Behaviour = _ => AnIcon() };
        AppIconResolver resolver = new(source);

        resolver.Request(
            "app",
            () => { builder = Environment.CurrentManagedThreadId; return [Good]; },
            () => { });

        Assert.True(Eventually(() => resolver.Lookup("app") is not null));
        Assert.NotEqual(caller, builder);
    }

    /// <summary>A healed pin must not keep serving an icon read out of the path it used to hold.</summary>
    [Fact]
    public void InvalidatingMakesItResolveAgain()
    {
        FakeSource source = new() { Behaviour = _ => AnIcon() };
        AppIconResolver resolver = new(source);

        resolver.Request("app", Candidates(Good), () => { });
        Assert.True(Eventually(() => resolver.Lookup("app") is not null));

        resolver.Invalidate("app");

        Assert.Null(resolver.Lookup("app"));
        Assert.True(resolver.Request("app", Candidates(Good), () => { }));
        Assert.True(Eventually(() => source.Asked.Count == 2));
    }

    /// <summary>A candidate list that is empty resolves to nothing rather than throwing.</summary>
    [Fact]
    public void NoCandidatesIsNotAnError()
    {
        FakeSource source = new() { Behaviour = _ => AnIcon() };
        AppIconResolver resolver = new(source, maxAttempts: 1);

        resolver.Request("app", () => [], () => { });

        Assert.True(Eventually(() => resolver.Exhausted("app")));
        Assert.Empty(source.Asked);
    }

    /// <summary>A source that throws is a failure, not a crash on a worker nobody is watching.</summary>
    [Fact]
    public void AThrowingSourceIsTreatedAsAFailure()
    {
        FakeSource source = new() { Behaviour = _ => throw new InvalidOperationException("nope") };
        AppIconResolver resolver = new(source, maxAttempts: 1);

        resolver.Request("app", Candidates(Good), () => { });

        Assert.True(Eventually(() => resolver.Exhausted("app")));
        Assert.Null(resolver.Lookup("app"));
    }

    /// <summary>
    /// <c>"file,index"</c> is how both a shortcut and a relaunch property spell an icon location.
    /// </summary>
    /// <remarks>
    /// The index is routinely negative — that is a resource ID, not an ordinal — so a parser that
    /// rejects or absolutes it would silently take the wrong icon out of <c>shell32.dll</c>.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\app\app.ico", @"C:\app\app.ico", 0)]
    [InlineData(@"C:\app\app.exe,3", @"C:\app\app.exe", 3)]
    [InlineData(@"%systemdir%\system32\shell32.dll,-128", @"%systemdir%\system32\shell32.dll", -128)]
    [InlineData(@"  C:\a b\x.dll , 2 ", @"C:\a b\x.dll", 2)]
    public void AnIconLocationSplitsIntoAFileAndAnIndex(string input, string path, int index)
    {
        IconCandidate parsed = ShellIconSource.Parse(input);

        Assert.Equal(path, parsed.Path);
        Assert.Equal(index, parsed.Index);
    }

    /// <summary>A path containing a comma but no index is a path, not a truncated one.</summary>
    [Fact]
    public void APathWhoseTailIsNotANumberIsLeftWhole()
    {
        Assert.Equal(@"C:\weird,name\app.exe", ShellIconSource.Parse(@"C:\weird,name\app.exe").Path);
    }
}
