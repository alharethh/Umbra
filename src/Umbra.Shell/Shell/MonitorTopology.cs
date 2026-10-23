using System.Globalization;
using System.IO;

namespace Umbra.Shell.Shell;

/// <summary>What changed between the screens a set was built for and the ones attached now.</summary>
internal enum TopologyChange
{
    /// <summary>The same monitors, the same primary. Nothing to do.</summary>
    None,

    /// <summary>
    /// Monitors went away and nothing else changed: no new one, and the primary is still there and
    /// still primary. The bar of a screen that has gone is drawn on no screen, so this can wait.
    /// </summary>
    RemovedOnly,

    /// <summary>A monitor arrived or the primary moved. Visible at once: a screen with no bar, or two stacked.</summary>
    Changed,
}

/// <summary>What <see cref="MonitorTopology.ClaimRebuild"/> decided. Only <see cref="Granted"/> restarts.</summary>
internal enum RebuildClaim
{
    /// <summary>Allowed, and recorded before the caller acts on it.</summary>
    Granted,

    /// <summary>The limit is reached.</summary>
    Limited,

    /// <summary>The record exists and cannot be read, so it counts as the limit reached.</summary>
    Unreadable,

    /// <summary>Allowed, but the record could not be written, so the next process would not see it.</summary>
    Unwritable,
}

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
    public static bool NeedsRebuild(bool perMonitorSet, IReadOnlyList<Display> builtFor, IReadOnlyList<Display> attached) =>
        Classify(perMonitorSet, builtFor, attached) != TopologyChange.None;

    /// <summary>
    /// What kind of change separates the screens attached now from the ones the set was built
    /// for. <see cref="TopologyChange.None"/> under the same conditions <see cref="NeedsRebuild"/>
    /// documents.
    /// </summary>
    public static TopologyChange Classify(bool perMonitorSet, IReadOnlyList<Display> builtFor, IReadOnlyList<Display> attached)
    {
        if (!perMonitorSet || builtFor.Count == 0 || attached.Count == 0) return TopologyChange.None;

        HashSet<string> before = new(builtFor.Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);
        HashSet<string> now = new(attached.Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);
        bool samePrimary = string.Equals(PrimaryOf(builtFor), PrimaryOf(attached), StringComparison.OrdinalIgnoreCase);

        if (before.SetEquals(now)) return samePrimary ? TopologyChange.None : TopologyChange.Changed;

        // A primary that went away is a change of primary as well, and ManagedShell then moves its
        // bar onto the new primary, on top of the bar already there: samePrimary is false then.
        return now.IsSubsetOf(before) && samePrimary ? TopologyChange.RemovedOnly : TopologyChange.Changed;
    }

    /// <summary>How long the displays must be quiet before a change is judged at all.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the displays must be quiet before a change that only removed monitors rebuilds.
    /// </summary>
    /// <remarks>
    /// A DisplayPort monitor leaves the topology when it sleeps, and a TV or a monitor re-checking
    /// its inputs drops out for a few seconds and comes back. Rebuilding at the first judgement
    /// would restart the bar twice for every such blink. A removed monitor's bar is drawn on no
    /// screen, so waiting costs nothing anyone can see, and a monitor that is back by then is no
    /// change at all.
    /// </remarks>
    public static readonly TimeSpan RemovalSettleDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How much longer to wait before acting on <paramref name="change"/>, given how long the
    /// displays have been quiet. Zero means act on it now.
    /// </summary>
    public static TimeSpan StillToSettle(TopologyChange change, TimeSpan quietFor) =>
        change == TopologyChange.RemovedOnly && quietFor < RemovalSettleDelay
            ? RemovalSettleDelay - quietFor
            : TimeSpan.Zero;

    /// <summary>At most this many rebuilds in any <see cref="RebuildWindow"/>.</summary>
    public const int RebuildsPerWindow = 2;

    /// <summary>
    /// The window the rebuild limit counts over. Two in ten minutes lets a real change and its
    /// undo through, a monitor unplugged and plugged back, and holds a monitor that flaps all
    /// night to twelve restarts an hour rather than one per flap.
    /// </summary>
    public static readonly TimeSpan RebuildWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long until a rebuild is allowed, given when the recent ones were. Zero means now.
    /// </summary>
    /// <remarks>
    /// <b>The limit has to outlive the process</b>, because each rebuild is a restart and a fresh
    /// process remembers nothing: the record is a file, as the hang relaunch's is. A time in the
    /// future, which is a clock that went backwards, is ignored rather than trusted, so a wrong
    /// clock can lift the limit for one window and can never stop the bar rebuilding.
    /// </remarks>
    public static TimeSpan RebuildWait(IReadOnlyList<DateTime> recentUtc, DateTime nowUtc)
    {
        List<DateTime> inWindow = [.. InWindow(recentUtc, nowUtc).Order()];
        if (inWindow.Count < RebuildsPerWindow) return TimeSpan.Zero;

        // The oldest of the last RebuildsPerWindow has to leave the window first.
        return inWindow[^RebuildsPerWindow] + RebuildWindow - nowUtc;
    }

    /// <summary>The record to keep after a rebuild at <paramref name="nowUtc"/>: the ones still in the window, then this one.</summary>
    public static IReadOnlyList<DateTime> WithRebuild(IReadOnlyList<DateTime> recentUtc, DateTime nowUtc) =>
        [.. InWindow(recentUtc, nowUtc).Order(), nowUtc];

    private static IEnumerable<DateTime> InWindow(IReadOnlyList<DateTime> recentUtc, DateTime nowUtc) =>
        recentUtc.Where(t => t <= nowUtc && nowUtc - t < RebuildWindow);

    /// <summary>Where the recent rebuilds are recorded.</summary>
    public static string DefaultRebuildStampPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Umbra",
        "topology-rebuilds");

    /// <summary>
    /// Reads the recorded rebuilds: none if there is no file, and null if there is one that cannot
    /// be read. Lines that do not parse are skipped.
    /// </summary>
    /// <remarks>
    /// <b>Null is not the same as empty.</b> A file locked by a scanner or a backup tool, or one
    /// with a broken ACL, says nothing about how many rebuilds there were, and reading it as none
    /// would lift the limit for as long as the file stays that way. <see cref="ClaimRebuild"/>
    /// treats it as the limit reached.
    /// </remarks>
    public static IReadOnlyList<DateTime>? ReadRebuilds(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];

            List<DateTime> times = [];
            foreach (string line in File.ReadAllLines(path))
            {
                if (long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) &&
                    ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
                    times.Add(new DateTime(ticks, DateTimeKind.Utc));
            }

            return times;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decides whether a rebuild may happen at <paramref name="nowUtc"/> and, if it may, records it
    /// first. The caller restarts only on <see cref="RebuildClaim.Granted"/>; otherwise
    /// <paramref name="wait"/> is how long until it judges again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every failure of the record fails toward not restarting.</b> The limit is the only thing
    /// between a flapping monitor and a restart every few seconds, and each restart reads the
    /// record afresh; a record that cannot be written is one the next process will not see, and a
    /// record that cannot be read could hold anything. So an unreadable record counts as the limit
    /// reached for one <see cref="RebuildWindow"/>, and a rebuild that cannot be recorded does not
    /// happen, and is judged again a window later. The bars stay as they are, which on a stacked
    /// set is wrong but still a working taskbar.
    /// </para>
    /// <para>
    /// A missing file is no record, not an unreadable one, and allows the rebuild.
    /// </para>
    /// </remarks>
    public static RebuildClaim ClaimRebuild(string path, DateTime nowUtc, out TimeSpan wait)
    {
        IReadOnlyList<DateTime>? recent = ReadRebuilds(path);
        if (recent is null)
        {
            wait = RebuildWindow;
            return RebuildClaim.Unreadable;
        }

        wait = RebuildWait(recent, nowUtc);
        if (wait > TimeSpan.Zero) return RebuildClaim.Limited;

        if (!WriteRebuilds(path, WithRebuild(recent, nowUtc)))
        {
            wait = RebuildWindow;
            return RebuildClaim.Unwritable;
        }

        return RebuildClaim.Granted;
    }

    /// <summary>Writes the record. A failure is reported, not thrown; <see cref="ClaimRebuild"/> then refuses the rebuild.</summary>
    public static bool WriteRebuilds(string path, IReadOnlyList<DateTime> timesUtc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, timesUtc.Select(t => t.Ticks.ToString(CultureInfo.InvariantCulture)));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A one-line description of a list of screens for the log, primary marked.</summary>
    public static string Describe(IReadOnlyList<Display> displays) =>
        displays.Count == 0
            ? "(none)"
            : string.Join(", ", displays.Select(d => d.Primary ? d.DeviceName + " (primary)" : d.DeviceName));

    private static string? PrimaryOf(IReadOnlyList<Display> displays) =>
        displays.FirstOrDefault(d => d.Primary).DeviceName;
}
