using Umbra.Core.Tiers;

namespace Umbra.Shell.Tasks;

/// <summary>What a slot's attention badge is doing.</summary>
internal enum AttentionState
{
    /// <summary>Nothing is asking. The badge is not drawn at all.</summary>
    None,

    /// <summary>Asking, and not yet ignored for long: <c>#B3B3B3</c>, 1 s on / 3 s off (§3.3).</summary>
    Pulsing,

    /// <summary>Ignored for 15 minutes: <c>#808080</c>, 1 s on / 6 s off (§3.3).</summary>
    Decayed,
}

/// <summary>
/// Whether an application is asking for attention, the taskbar flash of <c>FlashWindowEx</c>.
/// Pure, so the rule can be tested without a window.
/// </summary>
/// <remarks>
/// <para>
/// <b>The badge follows the request, not each flash.</b> Some applications flash forever
/// (<c>FLASHW_TIMERNOFG</c>), some flash a fixed number of times, and Windows itself keeps the
/// button highlighted after its seven flashes. ManagedShell turns all of them into one state,
/// <c>Flashing</c>, which it leaves when the window is activated; this latches on that state.
/// </para>
/// <para>
/// <b>Being switched to acknowledges it, even if the state lingers.</b> ManagedShell maintains
/// window state from shell events and it can go stale, which is why the running indicator asks
/// for the real foreground instead. A window that was in front and still reads <c>Flashing</c>
/// has been seen; the badge stays down until the application stops asking and asks again.
/// </para>
/// </remarks>
internal sealed class AttentionLatch
{
    private double? _since;
    private bool _acknowledged;

    /// <summary>Seconds on the caller's clock at which the badge decays, while one is up.</summary>
    public double? DecaysAt => _since is { } since && !_acknowledged
        ? since + NotificationBadge.DecayAfterSeconds
        : null;

    /// <summary>The badge's state now.</summary>
    /// <param name="flashing">Whether any of the application's windows is asking.</param>
    /// <param name="active">Whether one of its windows is the foreground window.</param>
    /// <param name="enabled">Windows' own "Show flashing on taskbar apps".</param>
    /// <param name="now">The caller's clock, in seconds.</param>
    public AttentionState Update(bool flashing, bool active, bool enabled, double now)
    {
        if (!flashing)
        {
            _since = null;
            _acknowledged = false;
            return AttentionState.None;
        }

        _since ??= now;
        if (active) _acknowledged = true;

        if (_acknowledged || !enabled) return AttentionState.None;

        return NotificationBadge.HasDecayed(now - _since.Value)
            ? AttentionState.Decayed
            : AttentionState.Pulsing;
    }
}
