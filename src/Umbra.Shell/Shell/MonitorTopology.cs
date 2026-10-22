namespace Umbra.Shell.Shell;

/// <summary>One attached monitor, as far as the bar set cares: which it is, and whether it is primary.</summary>
internal readonly record struct Display(string DeviceName, bool Primary);

/// <summary>
/// Decides whether a bar-per-monitor set has to be rebuilt because the monitors changed under it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The set is built once, at launch, one bar per screen, and nothing rebuilt it.</b> A monitor
/// plugged in got no bar, an unplugged one left its bar registered against a screen that no longer
/// exists, and changing which display is primary stacked two bars on one screen with no settings
/// change at all. That last one is ManagedShell's doing: on a display change it moves the bar whose
/// screen <em>was</em> primary to whichever screen is primary now, and leaves every other bar alone,
/// so the old primary's bar lands on the new primary, on top of the bar already there.
/// </para>
/// <para>
/// <b>Only the set of monitors and which one is primary count, never their size.</b> An exclusive
/// fullscreen game that switches resolution raises the same display-change message as a monitor
/// being unplugged; treating that as a topology change would rebuild the bars under every such game,
/// twice. ManagedShell already follows a size change on the primary.
/// </para>
/// <para>
/// <b>Compared against the screens the set was built for, not against the bars' current screens.</b>
/// ManagedShell rewrites the old primary bar's screen during the very change being judged, so asking
/// the bars would report the stacked state as the intended one.
/// </para>
/// </remarks>
internal static class MonitorTopology
{
    /// <summary>
    /// Whether the bars have to be rebuilt to put one on every attached monitor again.
    /// </summary>
    /// <param name="perMonitorSet">Whether the bars are a bar-per-monitor set. A lone bar is
    /// never rebuilt here: it follows the primary through ManagedShell, as it always has.</param>
    /// <param name="builtFor">The screens the set was built for, at launch.</param>
    /// <param name="attached">The screens attached now.</param>
    /// <remarks>
    /// Rebuilding re-registers every AppBar and reflows every maximised window, which §6.3 allows
    /// only when something actually moved. So an empty or unreadable list, which is what an
    /// enumeration in the middle of a change can return, is never a reason to rebuild.
    /// </remarks>
    public static bool NeedsRebuild(bool perMonitorSet, IReadOnlyList<Display> builtFor, IReadOnlyList<Display> attached)
    {
        if (!perMonitorSet || builtFor.Count == 0 || attached.Count == 0) return false;

        HashSet<string> before = new(builtFor.Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);
        HashSet<string> now = new(attached.Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);
        if (!before.SetEquals(now)) return true;

        return !string.Equals(PrimaryOf(builtFor), PrimaryOf(attached), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A one-line description of a list of screens for the log, primary marked.</summary>
    public static string Describe(IReadOnlyList<Display> displays) =>
        displays.Count == 0
            ? "(none)"
            : string.Join(", ", displays.Select(d => d.Primary ? d.DeviceName + " (primary)" : d.DeviceName));

    private static string? PrimaryOf(IReadOnlyList<Display> displays) =>
        displays.FirstOrDefault(d => d.Primary).DeviceName;
}
