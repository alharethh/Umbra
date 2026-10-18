using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// The lock that stops a second Umbra starting.
/// </summary>
/// <remarks>
/// <para>
/// Worth testing rather than eyeballing, because the failure is delayed and lands somewhere else.
/// Two bars is the visible symptom; the damage is that the second instance captures Explorer's
/// taskbar state while the first has it suppressed, and then restores the user's taskbar to hidden
/// when it exits. By the time anyone notices, both Umbras are gone.
/// </para>
/// <para>
/// A mutex is owned by a <i>thread</i>, not a process, so a second claim has to be made from
/// another one — claiming twice on this thread would succeed recursively and prove nothing.
/// </para>
/// </remarks>
public class SingleInstanceTests
{
    private static string Unique() => $@"Local\umbra-test-{Guid.NewGuid():N}";

    /// <summary>Claims from a fresh thread, so the mutex's thread ownership is real.</summary>
    private static SingleInstance? ClaimElsewhere(string name, TimeSpan timeout = default)
    {
        SingleInstance? claimed = null;

        Thread thread = new(() => claimed = SingleInstance.Claim(name, timeout));
        thread.Start();
        thread.Join();

        return claimed;
    }

    [Fact]
    public void The_First_Instance_Gets_The_Lock()
    {
        string name = Unique();

        using SingleInstance? first = SingleInstance.Claim(name);

        Assert.NotNull(first);
        Assert.False(first.WasAbandoned);
    }

    [Fact]
    public void A_Second_Instance_Is_Refused()
    {
        string name = Unique();

        using SingleInstance? first = SingleInstance.Claim(name);
        Assert.NotNull(first);

        Assert.Null(ClaimElsewhere(name));
    }

    [Fact]
    public void The_Lock_Is_Free_Again_Once_The_First_Instance_Releases_It()
    {
        // The restart path depends on this: the replacement is waiting for exactly this handover.
        string name = Unique();

        SingleInstance? first = SingleInstance.Claim(name);
        Assert.NotNull(first);
        first.Dispose();

        // From another thread, or a Dispose that released nothing would still let this through on
        // the strength of recursive ownership.
        using SingleInstance? second = ClaimElsewhere(name);
        Assert.NotNull(second);
    }

    /// <summary>
    /// A restart waits for its predecessor rather than giving up instantly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two overlap on purpose. With a zero timeout on this path a restart would refuse itself
    /// whenever the outgoing process took a moment to let go, and the user would click "Restart
    /// Umbra" and be left with no bar at all.
    /// </para>
    /// <para>
    /// <b>The outgoing lock is taken on its own thread, and that is the whole test.</b> Taking it
    /// here would let the claim below succeed recursively — same thread, same mutex — and the test
    /// passes with the wait removed entirely. It did, when this was first written.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Restart_Waits_For_The_Outgoing_Instance()
    {
        string name = Unique();

        using ManualResetEventSlim claimed = new();
        using ManualResetEventSlim released = new();

        Thread outgoing = new(() =>
        {
            SingleInstance? held = SingleInstance.Claim(name);
            claimed.Set();

            Thread.Sleep(250);

            held?.Dispose();
            released.Set();
        });

        outgoing.Start();
        Assert.True(claimed.Wait(TimeSpan.FromSeconds(5)), "the outgoing instance never took the lock");
        Assert.False(released.IsSet, "the outgoing instance let go before the claim was even attempted");

        // The replacement, which must wait rather than refuse.
        using SingleInstance? replacement = SingleInstance.Claim(name, TimeSpan.FromSeconds(5));

        outgoing.Join();

        Assert.NotNull(replacement);
    }

    /// <summary>
    /// An abandoned lock is a successful claim, not a failure.
    /// </summary>
    /// <remarks>
    /// This is the one that matters after a hard kill. The obvious implementation lets
    /// <c>AbandonedMutexException</c> escape or treats it as "someone else has it", and the user is
    /// left unable to start Umbra again until they log out — with the taskbar still suppressed,
    /// because the process that suppressed it never got to put it back.
    /// </remarks>
    [Fact]
    public void An_Abandoned_Lock_Is_Claimed_And_Reported()
    {
        string name = Unique();

        // A thread that takes the lock and dies without releasing it, exactly as a killed process
        // does.
        //
        // Two races, both of which made this flaky under a parallel run. The dead thread's claim
        // is kept reachable, because finalising its Mutex closes the last handle and destroys the
        // named object, so the next claim would create a fresh one that was never abandoned. And
        // the next claim waits a little, because Thread.Join returns when the managed thread is
        // done, which can be a moment before the OS thread has exited and abandoned the lock.
        SingleInstance? dead = null;
        Thread killed = new(() => dead = SingleInstance.Claim(name));
        killed.Start();
        killed.Join();

        using SingleInstance? next = SingleInstance.Claim(name, TimeSpan.FromSeconds(5));
        GC.KeepAlive(dead);

        Assert.NotNull(next);
        Assert.True(next.WasAbandoned, "an abandoned lock should be reported as such, not silently taken");
    }

    [Fact]
    public void Disposing_Twice_Is_Harmless()
    {
        // OnExit runs after a Shutdown that may itself have been triggered by a failed claim.
        string name = Unique();

        SingleInstance? instance = SingleInstance.Claim(name);
        Assert.NotNull(instance);

        instance.Dispose();
        instance.Dispose();
    }
}
