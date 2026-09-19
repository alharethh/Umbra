namespace Umbra.Shell.Tasks;

/// <summary>
/// The order of the icon row: one list covering pinned and running applications alike.
/// </summary>
/// <remarks>
/// <para>
/// The bar used to lay out as <i>pins in pin order, then everything else that is running</i>. That
/// segregation is what made pinning an application jump it to the far left — it left the running
/// tail and joined the end of the pinned section — and it is why dragging an unpinned icon had to
/// be refused: the running tail had no stored order for a drag to rewrite. Windows does not
/// segregate the two, and neither does this. One order, every icon in it, anything may be dragged
/// anywhere.
/// </para>
/// <para>
/// <b>An icon holds its place for as long as it is pinned or running, and joins the end when it
/// first appears.</b> That is the whole rule, and it settles the question the segregated layout
/// dodged: an unpinned application that closes leaves the row, so when it reopens it is new and
/// goes to the end. Its old position is not held — nothing is drawn there to hold, and a row that
/// silently reserved gaps for applications that are not running would be a worse surprise than the
/// one this replaces. It is also what the Windows taskbar does. Pin it if you want it to stay put;
/// that is what pinning is for.
/// </para>
/// <para>
/// Deliberately free of ManagedShell and of WPF: this is the piece that decides where every icon
/// sits, and it is worth being able to test without a window.
/// </para>
/// </remarks>
internal sealed class BarOrder
{
    private readonly List<string> _keys = [];

    /// <summary>The row, in display order.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>How many icons the row holds.</summary>
    public int Count => _keys.Count;

    /// <summary>Where an application sits, or -1 if it is not on the bar.</summary>
    public int IndexOf(string? key) => _keys.FindIndex(k => AppIdentity.Same(k, key));

    /// <summary>
    /// Brings the row into line with what is pinned and what is running.
    /// </summary>
    /// <param name="pinned">Pinned applications, in the order the pin file holds them.</param>
    /// <param name="running">Applications with at least one window open, in shell order.</param>
    /// <returns>True when the row changed.</returns>
    /// <remarks>
    /// Everything already on the bar keeps its position; only departures and arrivals move
    /// anything. <paramref name="pinned"/> is consumed before <paramref name="running"/> so that on
    /// a cold start — when the row is empty and every key is an arrival — the pin file's order is
    /// what the user sees, with whatever else happens to be running behind it.
    /// </remarks>
    public bool Reconcile(IReadOnlyList<string> pinned, IReadOnlyCollection<string> running)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(running);

        int removed = _keys.RemoveAll(k =>
            !pinned.Any(p => AppIdentity.Same(p, k)) && !running.Any(r => AppIdentity.Same(r, k)));

        int added = 0;
        foreach (string key in pinned.Concat(running))
        {
            if (IndexOf(key) >= 0) continue;

            _keys.Add(key);
            added++;
        }

        return removed > 0 || added > 0;
    }

    /// <summary>Moves an application to a new position, for drag-reordering.</summary>
    /// <param name="key">The application to move.</param>
    /// <param name="index">Where to move it to, clamped to the row. A drag is not obliged to stay
    /// inside the bar and routinely runs off both ends.</param>
    /// <returns>True when the order actually changed.</returns>
    public bool Move(string key, int index)
    {
        int current = IndexOf(key);
        if (current < 0) return false;

        index = Math.Clamp(index, 0, _keys.Count - 1);
        if (index == current) return false;

        string moved = _keys[current];
        _keys.RemoveAt(current);
        _keys.Insert(index, moved);

        return true;
    }

    /// <summary>
    /// Renames an application in place, keeping its position.
    /// </summary>
    /// <returns>True when the row changed.</returns>
    /// <remarks>
    /// For an application that has updated into a new directory and so changed identity. Renaming
    /// rather than letting <see cref="Reconcile"/> drop the old key and append the new one matters:
    /// appending would send a pinned icon the user had placed deliberately to the end of the row,
    /// once, mysteriously, on whichever launch followed an update. Where both identities are
    /// already present the position kept is the <b>old</b> one, because that is the one the user
    /// chose.
    /// </remarks>
    public bool Rename(string oldKey, string newKey)
    {
        int current = IndexOf(oldKey);
        if (current < 0) return false;
        if (AppIdentity.Same(oldKey, newKey)) return false;

        int existing = IndexOf(newKey);
        if (existing >= 0)
        {
            _keys.RemoveAt(existing);
            if (existing < current) current--;
        }

        _keys[current] = newKey;
        return true;
    }

    /// <summary>
    /// The pinned applications, in the order the row shows them.
    /// </summary>
    /// <remarks>
    /// This is what gets written to the pin file. The file can only record pins, so a row of
    /// <c>pinned, running, pinned</c> persists as two pins in that relative order — the running
    /// icon between them is simply not there on the next launch, and the pins close up. Recording
    /// the relative order is the part that matters and the part the segregated layout could not
    /// express, because a pin dragged past a running icon had nowhere to say so.
    /// </remarks>
    public List<string> PinnedIn(Func<string, bool> isPinned)
    {
        ArgumentNullException.ThrowIfNull(isPinned);

        return [.. _keys.Where(isPinned)];
    }
}
