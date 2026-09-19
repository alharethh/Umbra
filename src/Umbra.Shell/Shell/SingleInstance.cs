namespace Umbra.Shell.Shell;

/// <summary>
/// The claim that makes this process <i>the</i> bar.
/// </summary>
/// <remarks>
/// <para>
/// Two Umbras at once is not a cosmetic problem. Both register a space-reserving AppBar, both
/// suppress Explorer's taskbar, and both capture what they think the user's taskbar state was —
/// and the second one captures it <b>already suppressed</b>, so on its exit it faithfully restores
/// the user's taskbar to hidden. That is the same silent failure <c>ExplorerStateGuard</c> exists
/// for, and it outlives the process that caused it.
/// </para>
/// <para>
/// A named mutex rather than looking for another Umbra process: no name collisions, no permission
/// question when enumerating, and the kernel settles the race between two launches rather than
/// leaving both to check-then-act and both to win.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// The lock's name. <c>Local\</c> is per session by design: two users logged in at once have a
    /// desktop each and should get a bar each.
    /// </summary>
    public const string DefaultName = @"Local\Umbra.SingleInstance";

    private Mutex? _mutex;

    private SingleInstance(Mutex mutex, bool abandoned)
    {
        _mutex = mutex;
        WasAbandoned = abandoned;
    }

    /// <summary>
    /// Whether the lock was picked up off a process that died without releasing it.
    /// </summary>
    /// <remarks>
    /// Worth recording rather than swallowing: it says the last exit was not clean, which is the
    /// same condition <c>ExplorerStateGuard</c> recovers the taskbar from, and the two should agree.
    /// </remarks>
    public bool WasAbandoned { get; }

    /// <summary>Takes the lock, or returns null if another instance already holds it.</summary>
    /// <param name="name">Lock name; defaults to <see cref="DefaultName"/>.</param>
    /// <param name="timeout">
    /// How long to wait for it. Zero for an ordinary launch — if a bar is already running, there is
    /// nothing to wait for. A restart passes a grace period instead, because it is deliberately
    /// racing its own predecessor out of the door.
    /// </param>
    public static SingleInstance? Claim(string? name = null, TimeSpan timeout = default)
    {
        Mutex mutex = new(initiallyOwned: false, name ?? DefaultName);

        bool held;
        bool abandoned = false;

        try
        {
            held = mutex.WaitOne(timeout, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // An abandoned mutex is still acquired by the waiter that observes it. This is a
            // successful claim, not a failure, and treating it as one would leave the user with no
            // bar at all after any unclean exit.
            held = true;
            abandoned = true;
        }

        if (held) return new SingleInstance(mutex, abandoned);

        mutex.Dispose();
        return null;
    }

    /// <summary>Releases the lock. Must run on the thread that claimed it.</summary>
    public void Dispose()
    {
        if (_mutex is null) return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread. The handle close below hands it back regardless; the only
            // cost is that the next instance sees it as abandoned rather than released.
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
