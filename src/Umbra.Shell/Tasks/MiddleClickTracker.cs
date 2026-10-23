namespace Umbra.Shell.Tasks;

/// <summary>
/// Pairs a middle button-up with the button-down that began it, so a release launches only over
/// the icon the same press began on.
/// </summary>
/// <remarks>
/// <para>
/// The bar reports <b>every</b> middle press and release here, not only those on an icon: a
/// press or release anywhere else is reported with no target. That is the whole point. Recording
/// only presses on icons, and clearing only on releases over icons, left a cancelled press
/// standing: press on Chrome, slide off and release over empty bar, then press on empty bar,
/// slide onto Chrome and release, and Chrome launched from a click that never began on it.
/// </para>
/// <para>
/// The bar's window-level handler reports each event with no target <em>and</em> the icon's own
/// handler reports it again with the icon, in that order for a press (the window's tunnelling
/// handler runs first) and the other order for a release (the icon's bubbling handler runs first).
/// Both orders give the right answer: a press with no target clears, the icon's report then sets;
/// a release consumes the press, so the window's report afterwards finds nothing.
/// </para>
/// </remarks>
internal sealed class MiddleClickTracker<T> where T : class
{
    private T? _pressed;

    /// <summary>A middle press, on <paramref name="target"/> or, when null, on nothing that launches.</summary>
    public void Press(T? target) => _pressed = target;

    /// <summary>
    /// A middle release over <paramref name="target"/>, or over nothing when null. True when the
    /// press it ends began on that same target. Always ends the press, whatever it answers.
    /// </summary>
    public bool Release(T? target)
    {
        T? pressed = _pressed;
        _pressed = null;
        return target is not null && ReferenceEquals(pressed, target);
    }

    /// <summary>The pointer left the bar, or something else ended the press without a release here.</summary>
    public void Cancel() => _pressed = null;
}
