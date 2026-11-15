namespace Umbra.Shell.Tasks;

/// <summary>
/// Decides when a pin whose application looks uninstalled is really gone: two looks at least a
/// minute apart that both found it gone, and nothing else in between.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two looks.</b> One look can catch an application in the middle of something: an installer
/// replacing its files, a drive still mounting at sign-in, a package API that answered too early.
/// Removing a pin the user wanted is far worse than showing a dead one for another minute, so a
/// single answer never removes anything. The owner's call, 2026-11-15: "remove once confirmed",
/// for every pin, checked twice a minute apart.
/// </para>
/// <para>
/// <b>Anything but Gone starts over.</b> Present or Unknown between the two looks clears the
/// record, so the minute is a minute of unbroken absence. A pin is looked at no more than every
/// 30 s otherwise, because the model asks on every window event on the desktop.
/// </para>
/// <para>
/// Pure: the clock is passed in, in milliseconds, and nothing here touches the disk.
/// </para>
/// </remarks>
internal sealed class DeadPinWatch
{
    /// <summary>How long an application must stay gone before its pin is removed.</summary>
    public static readonly TimeSpan Confirmation = TimeSpan.FromSeconds(60);

    /// <summary>How often a pin that is not under suspicion is looked at.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, Record> _records = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The earliest time a look that was asked for too soon becomes due, since it was last reported.</summary>
    private long? _skippedDue;

    private readonly record struct Record(long LastLook, long? GoneSince);

    /// <summary>
    /// Looks at a pin if it is due, and says whether to remove it now.
    /// </summary>
    /// <param name="key">The pin's key.</param>
    /// <param name="now">Milliseconds on a monotonic clock.</param>
    /// <param name="look">The look itself; called only when one is due.</param>
    public bool Look(string key, long now, Func<Presence> look)
    {
        bool known = _records.TryGetValue(key, out Record record);

        if (known)
        {
            long due = record.GoneSince is { } since
                ? since + (long)Confirmation.TotalMilliseconds
                : record.LastLook + (long)Interval.TotalMilliseconds;

            if (now < due)
            {
                // Asked for, and too soon. Remember when it is due, so the model can come back then
                // rather than at whatever window event is next: an uninstaller's window closing
                // within 30 s of the last look would otherwise be the last word for a long time.
                _skippedDue = _skippedDue is null ? due : Math.Min(_skippedDue.Value, due);
                return false;
            }
        }

        if (look() != Presence.Gone)
        {
            _records[key] = new Record(now, null);
            return false;
        }

        if (known && record.GoneSince is { } goneSince && now - goneSince >= Confirmation.TotalMilliseconds)
        {
            _records.Remove(key);
            return true;
        }

        _records[key] = new Record(now, now);
        return false;
    }

    /// <summary>The application is running: whatever was suspected, it is there.</summary>
    public void Running(string key, long now) => _records[key] = new Record(now, null);

    /// <summary>Drops records of keys that are no longer pinned, after an unpin or a re-key.</summary>
    public void Keep(IEnumerable<string> pinnedKeys)
    {
        HashSet<string> keep = new(pinnedKeys, StringComparer.OrdinalIgnoreCase);
        foreach (string key in _records.Keys.Where(k => !keep.Contains(k)).ToList())
            _records.Remove(key);
    }

    /// <summary>
    /// Milliseconds until a look is owed: a suspected pin's second look, or one that was asked for
    /// too soon. Null when none is. Reporting a skipped look clears it; the caller arms a timer.
    /// </summary>
    public long? NextLookIn(long now)
    {
        long? earliest = _skippedDue is { } skipped ? Math.Max(0, skipped - now) : null;
        _skippedDue = null;

        foreach (Record record in _records.Values)
        {
            if (record.GoneSince is not { } since) continue;

            long due = Math.Max(0, since + (long)Confirmation.TotalMilliseconds - now);
            earliest = earliest is null ? due : Math.Min(earliest.Value, due);
        }

        return earliest;
    }
}
