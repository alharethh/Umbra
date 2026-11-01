namespace Umbra.Shell.Shell;

/// <summary>What one tick of <see cref="FullScreenSurfacing"/> asks the bar to do.</summary>
internal enum SurfacingChange
{
    None,

    /// <summary>Come up over the fullscreen application.</summary>
    Raise,

    /// <summary>Give way to it again.</summary>
    Lower,
}

/// <summary>
/// Decides when a bar that has given way to a fullscreen application comes up over it, and when
/// it goes back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule, owner's call 2026-11-01:</b> Win brings the bar up with Start, and Win again takes
/// it away with Start — as long as the bar was not touched. Once it is pressed, it stays until a
/// press lands somewhere that is neither the bar nor one of its menus.
/// </para>
/// <para>
/// <b>Following Start alone made the bar unusable over a game.</b> Nearly every press on the bar
/// closes Start on purpose — a right-click so the icon menu is not drawn under it, the volume
/// cluster because Quick Settings replaces it — so the bar gave way on the next tick and took the
/// menu the user was opening with it. Reported as "it simply disappears after any click on it".
/// </para>
/// <para>
/// Pure, so the rule can be asserted without a fullscreen window. The bar reports presses on
/// itself as they happen (<see cref="BarPressed"/>), because a press there is routed to it and
/// can be shorter than a tick; presses elsewhere are only seen by polling, so they arrive with the
/// tick.
/// </para>
/// </remarks>
internal sealed class FullScreenSurfacing
{
    /// <summary>Whether the bar was pressed while up, and so stays until a press elsewhere.</summary>
    private bool _held;

    /// <summary>Whether the bar is currently up over the fullscreen application.</summary>
    public bool Raised { get; private set; }

    /// <summary>A press landed on the bar.</summary>
    /// <remarks>Ignored while the bar has given way: then it is under the game and cannot be the target.</remarks>
    public void BarPressed()
    {
        if (Raised) _held = true;
    }

    /// <summary>Re-evaluates, once per tick, while a fullscreen application holds the bar's screen.</summary>
    /// <param name="startOpen">Whether Start is in front.</param>
    /// <param name="pressedElsewhere">
    /// Whether a press began since the last tick on something that is neither the bar, one of its
    /// panels, nor a surface the bar opened.
    /// </param>
    public SurfacingChange Tick(bool startOpen, bool pressedElsewhere)
    {
        if (pressedElsewhere) _held = false;

        bool raise = startOpen || _held;
        if (raise == Raised) return SurfacingChange.None;

        Raised = raise;
        return raise ? SurfacingChange.Raise : SurfacingChange.Lower;
    }

    /// <summary>The fullscreen application has gone; nothing is conceded and nothing is held.</summary>
    public void Reset()
    {
        Raised = false;
        _held = false;
    }
}
