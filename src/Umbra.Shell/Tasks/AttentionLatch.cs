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
/// <b>The window's state is each flash, not the request.</b> <c>FlashWindowEx</c> toggles the
/// button at the caret-blink rate, and ManagedShell follows every toggle: <c>HSHELL_FLASH</c> sets
/// <c>Flashing</c> and the <c>HSHELL_REDRAW</c> that turns it off again puts the window back to
/// <c>Inactive</c> (<c>TasksService.ShellWinProc</c>). Latching on the state level therefore
/// restarted the badge about once a second, drawing the flash cadence at ~45% duty and resetting
/// the 15-minute decay before it could ever arrive.
/// </para>
/// <para>
/// <b>So a flash opens the request, and it holds through the gaps.</b> It ends when one of the
/// application's windows is switched to, when every window that flashed has closed, or when no
/// window has been lit for <see cref="QuietSeconds"/>.
/// </para>
/// <para>
/// <b>Unlit for that long means withdrawn, and a counted flash is not that.</b> Measured
/// 2026-10-29 with a shell hook on a minimised window: <c>FLASHW_ALL</c> with a count of 5
/// alternates <c>HSHELL_FLASH</c> and <c>HSHELL_REDRAW</c> about 530 ms apart and ends on
/// <c>HSHELL_FLASH</c>, so the window stays <c>Flashing</c>, as Windows leaves the button lit.
/// <c>FLASHW_STOP</c> sends one <c>HSHELL_REDRAW</c> and nothing after it: <c>Inactive</c> for
/// good. Electron's <c>flashFrame(false)</c> and Chrome withdraw this way, for example when a
/// message is read on another device, and the dot used to pulse on all night.
/// </para>
/// <para>
/// <b>Being switched to acknowledges it for the whole application</b>, every window that is
/// asking included, as the slot is one button. Flashes that keep coming after that are the same
/// request still blinking (<c>FLASHW_TIMER</c> does not stop on activation) and do not bring the
/// badge back. A flash after <see cref="QuietSeconds"/> without one is a new request. So a second
/// window that starts asking while an acknowledged one is still blinking is folded into the
/// acknowledged request and does not show: the slot has one dot, and the request it would raise
/// is indistinguishable from the blinking that is already there.
/// </para>
/// </remarks>
internal sealed class AttentionLatch
{
    /// <summary>
    /// How long without a lit window before the request is withdrawn, or the next flash counts as
    /// a new one. The default toggle is the caret-blink time, 530 ms; this leaves room for a slower
    /// <c>dwTimeout</c>.
    /// </summary>
    public const double QuietSeconds = 3.0;

    private readonly HashSet<IntPtr> _askers = [];
    private double? _since;
    private double _lastFlash = double.NegativeInfinity;
    private bool _flashingNow;
    private bool _acknowledged;

    /// <summary>Seconds on the caller's clock at which the badge decays, while one is up.</summary>
    public double? DecaysAt => _since is { } since && !_acknowledged
        ? since + NotificationBadge.DecayAfterSeconds
        : null;

    /// <summary>
    /// Seconds on the caller's clock at which a badge that is up ends unless a window lights again.
    /// </summary>
    /// <remarks>
    /// Nothing else asks again at that moment: a withdrawn flash is one last
    /// <c>HSHELL_REDRAW</c> and then silence, so the caller has to wake itself to see it.
    /// </remarks>
    public double? WithdrawsAt => _since is not null && !_acknowledged && !_flashingNow
        ? _lastFlash + QuietSeconds
        : null;

    /// <summary>The badge's state now.</summary>
    /// <param name="open">The application's windows that are open.</param>
    /// <param name="flashing">Those of them whose button is lit at this instant.</param>
    /// <param name="active">Whether one of its windows is the foreground window.</param>
    /// <param name="now">The caller's clock, in seconds.</param>
    public AttentionState Update(
        IReadOnlyCollection<IntPtr> open, IReadOnlyCollection<IntPtr> flashing, bool active, double now)
    {
        // A request whose every asking window has closed is over.
        _askers.RemoveWhere(h => !open.Contains(h));
        if (_since is not null && _askers.Count == 0) Clear();

        // A window that was lit at the last update has been lit until now. A counted flash ends
        // lit and nothing is heard until it changes, possibly minutes later, so the quiet spell is
        // measured from the moment it went dark, not from the last update that saw it lit.
        // Otherwise a lit window starting a fresh flash, which opens with an off-toggle, would end
        // its own request on the spot.
        if (_flashingNow && flashing.Count == 0) _lastFlash = now;

        // And a request nothing has lit for a while is over: the application took it back.
        if (_since is not null && flashing.Count == 0 && now - _lastFlash > QuietSeconds) Clear();

        _flashingNow = flashing.Count > 0;

        if (flashing.Count > 0)
        {
            if (_acknowledged && now - _lastFlash > QuietSeconds) Clear();

            _lastFlash = now;
            _since ??= now;
            _askers.UnionWith(flashing);
        }

        if (_since is null) return AttentionState.None;

        if (active) _acknowledged = true;

        if (_acknowledged) return AttentionState.None;

        return NotificationBadge.HasDecayed(now - _since.Value)
            ? AttentionState.Decayed
            : AttentionState.Pulsing;
    }

    private void Clear()
    {
        _since = null;
        _acknowledged = false;
        _askers.Clear();
    }
}
