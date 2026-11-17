namespace Umbra.Shell.Shell;

/// <summary>What one look at a held bar asks it to do.</summary>
internal enum SameAppChange
{
    /// <summary>Go on giving way.</summary>
    Keep,

    /// <summary>ManagedShell lists a fullscreen application on this screen again; the hold is no longer needed.</summary>
    Rejoin,

    /// <summary>The application that held the bar down is no longer in front, or no longer fullscreen; come up.</summary>
    Rise,
}

/// <summary>
/// What the bar knows about the fullscreen window it gave way to, at one moment.
/// </summary>
/// <param name="ConcededProcess">The process that owns the fullscreen window; 0 if the window has gone.</param>
/// <param name="ForegroundProcess">The process that owns the foreground window; 0 if there is none.</param>
/// <param name="StillFullScreen">Whether ManagedShell's own check still calls that window fullscreen on this screen.</param>
internal readonly record struct SameAppFacts(uint ConcededProcess, uint ForegroundProcess, bool StillFullScreen);

/// <summary>
/// Keeps the bar down while a fullscreen application's own smaller window is in front.
/// </summary>
/// <remarks>
/// <para>
/// <b>The report, 2026-11-17:</b> clicking play/pause in fullscreen VLC brought the bar up. VLC's
/// controls are a separate window (a layered tool window, not topmost), and clicking them makes it
/// the foreground. ManagedShell's <c>FullScreenHelper</c> moves a fullscreen app that is not in
/// front to its inactive list whenever the foreground window shares its screen and is not topmost,
/// so its list emptied and the bar came up, about 1.1 s, until VLC handed the foreground back to
/// its video window. The bar is as tall as the controls and sits at the same edge, so for that
/// second it covered them, and a second click landed on the bar.
/// </para>
/// <para>
/// <b>The rule:</b> when ManagedShell says to come up, the bar keeps giving way if the foreground
/// belongs to the same process as the window it gave way to, and that window is still fullscreen
/// by ManagedShell's own check. It looks again on every tick, and comes up the moment either stops
/// being true: another application in front, the player closed, fullscreen left. A game's own
/// settings dialog over the game counts as the game, and the bar stays down for it too.
/// </para>
/// <para>
/// <b>ManagedShell's own check, not a second detector</b>, for the reason
/// <see cref="FullScreenRecheck"/> gives: if we called a window fullscreen and ManagedShell did
/// not, nothing would ever bring the bar back.
/// </para>
/// <para>
/// Pure, so the rule can be asserted without a fullscreen window.
/// </para>
/// </remarks>
internal sealed class SameAppHold
{
    /// <summary>Whether the bar is staying down although ManagedShell told it to come up.</summary>
    public bool Holding { get; private set; }

    /// <summary>Whether a bar told to come up should keep giving way.</summary>
    public static bool KeepsGivingWay(SameAppFacts facts) =>

        // Both 0 is "the window has gone" and "nothing is in front", not one application.
        facts.ConcededProcess != 0
        && facts.ForegroundProcess == facts.ConcededProcess
        && facts.StillFullScreen;

    /// <summary>ManagedShell has told the bar to come up. True if it should stay down instead.</summary>
    public bool OnLeave(SameAppFacts facts)
    {
        Holding = KeepsGivingWay(facts);
        return Holding;
    }

    /// <summary>Looks again, once per tick, while holding.</summary>
    /// <param name="listedAgain">Whether ManagedShell lists a fullscreen application on this screen again.</param>
    /// <param name="facts">The window as it is now.</param>
    public SameAppChange Tick(bool listedAgain, SameAppFacts facts)
    {
        if (!Holding) return SameAppChange.Keep;

        if (listedAgain)
        {
            Holding = false;
            return SameAppChange.Rejoin;
        }

        if (KeepsGivingWay(facts)) return SameAppChange.Keep;

        Holding = false;
        return SameAppChange.Rise;
    }

    /// <summary>Something else has taken over the bar's position (Start raised it, or it really left).</summary>
    public void Reset() => Holding = false;
}
