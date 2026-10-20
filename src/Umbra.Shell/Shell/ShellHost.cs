using ManagedShell;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Logging;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Owns the ManagedShell services Umbra runs on, and the lifetime of Explorer's suppression.
/// </summary>
/// <remarks>
/// Build order step 1 takes the tasks service and the AppBar machinery only. The tray is step 7 —
/// it is left switched off here so that the two 100 ms polling timers shell-integration §2.3
/// describes are not both running while the bar's basic behaviour is being characterised. Only the
/// taskbar-visibility watchdog runs at this stage; the tray Z-order timer arrives with the tray.
/// </remarks>
internal sealed class ShellHost : IDisposable
{
    private readonly ExplorerStateGuard _explorerState;
    private bool _disposed;

    /// <summary>
    /// The notification icons that stay out of the overflow: volume, network, power, action centre.
    /// </summary>
    /// <remarks>
    /// Identified by the fixed GUIDs Windows gives its own indicators, not by path — every one of
    /// them is owned by <c>explorer.exe</c>, so a path would match all four and nothing else.
    /// </remarks>
    private static readonly string[] SystemIndicators =
    [
        ManagedShell.WindowsTray.NotificationArea.VOLUME_GUID,
        ManagedShell.WindowsTray.NotificationArea.NETWORK_GUID,
        ManagedShell.WindowsTray.NotificationArea.POWER_GUID,
        ManagedShell.WindowsTray.NotificationArea.HEALTH_GUID,
    ];

    /// <param name="enableTray">
    /// <b>This works.</b> It was believed not to — recorded as "Windows 11 25H2 does not deliver
    /// notification icons this way" — and that was wrong. Windows routes them correctly; the icons
    /// were being thrown away inside ManagedShell by a null <c>PinnedNotifyIcons</c>, silently. See
    /// docs/measurements.md step 7 for the full chain and how it was finally caught.
    /// <para>
    /// <b>On by default since 2026-09-20</b>, by deliberate choice. It costs a permanent
    /// 100 ms timer and §11.2 Q2 — whether ManagedShell's timers defeat display idle — is still
    /// unmeasured; that is a power question to answer, not a reason to ship a taskbar with no
    /// notification area.
    /// </para>
    /// </param>
    public ShellHost(bool enableTray = true)
    {
        // Before anything else. ManagedShell reports its own failures to ShellLogger and carries
        // on, so an unattached logger turns every internal fault into silence — which is exactly
        // how a null PinnedNotifyIcons cost this project its tray and a long investigation. There
        // is no reason ever to run it deaf.
        ShellLogger.Attach(new BringUpLogBridge());

        ShellConfig config = new()
        {
            EnableTasksService = true,
            AutoStartTasksService = true,

            // One bar on one screen for now. Per-monitor placement is spec §11.2 Q12 and is not
            // settled; until it is, the bar shows every window rather than filtering by monitor.
            MultiMonAwareTasksService = false,

            // 32 px source art down to the 24 px the spec draws at (§7.1). Small is 16 px, and
            // upscaling it would put a soft icon under a normaliser that measures percentiles.
            TaskIconSize = IconSize.Large,

            // Tray hosting (build order step 7). This is the second of the two permanent 100 ms
            // timers shell-integration §2.3 describes — the first re-hides Explorer's taskbar, this
            // one re-asserts our tray window's Z-order. Both are outside our control and both bear
            // on §11.2 Q2, whether they prevent the display from ever idling.
            EnableTrayService = enableTray,
            AutoStartTrayService = enableTray,

            // <b>Load-bearing, and its absence is silent.</b> ManagedShell 0.0.370 leaves this
            // null unless the host sets it, and NotifyIcon.SetPinValues() dereferences it for
            // every icon that arrives — so every icon threw a NullReferenceException that
            // SysTrayCallback caught and reported only to ShellLogger, which nothing was
            // listening to. The tray collected nothing and said nothing, for months.
            //
            // The four system indicators, which is what Windows keeps out of the overflow and
            // what ManagedShell itself defaults to. Everything else goes behind the chevron.
            // Measured on this machine: the volume icon really does arrive carrying VOLUME_GUID,
            // as an ordinary notify icon owned by explorer.exe. The network one does not arrive
            // at all — it is listed anyway so that it lands in the right place if it ever does.
            PinnedNotifyIcons = SystemIndicators,

            EnableWin11DpiWorkaround = false,
        };

        ShellManager = new ShellManager(config);

        _explorerState = new ExplorerStateGuard(new ExplorerTaskbarState(ShellManager.ExplorerHelper));
        _explorerState.Capture();

        // §7.0: point Explorer's alignment at ours so the Start menu opens beneath our Start
        // button. Umbra is left-aligned by default, which is TaskbarAl = 0. Restored on exit, and
        // after a hard kill by the same marker file that restores the autohide state.
        _explorerState.ApplyAlignment(0);

        // The load-bearing call. This sets Explorer's taskbar to autohide, which is what releases
        // its reserved work area — without it both bars reserve space and every maximised window
        // comes up short by two taskbar heights.
        ShellManager.ExplorerHelper.HideExplorerTaskbar = true;

        ShellManager.Tasks.Initialize();

        if (enableTray)
        {
            // Claims the tray and broadcasts TaskbarCreated, which is what makes every running
            // application re-add its notification icon. AutoStartTrayService has already done this,
            // and the repeat is not merely harmless — TrayService.Initialize is guarded, so the
            // second call costs nothing but the second TaskbarCreated broadcast, which is what
            // actually populates the tray. Measured: zero icons after the constructor, eight after
            // this call.
            ShellManager.NotificationArea.Initialize();

            Diagnostics.BringUpLog.Write(
                $"tray: handle=0x{ShellManager.NotificationArea.Handle.ToInt64():X} " +
                $"failed={ShellManager.NotificationArea.IsFailed} " +
                $"icons={ShellManager.NotificationArea.TrayIcons.Count}");

        }
    }

    public ShellManager ShellManager { get; }

    /// <summary>True when startup had to repair a previous run's unclean exit.</summary>
    public bool RecoveredFromUncleanExit => _explorerState.RecoveredFromUncleanExit;

    /// <summary>
    /// Puts Explorer back. Idempotent, and deliberately callable from an unhandled-exception
    /// handler where disposing the whole shell may not be safe.
    /// </summary>
    public void RestoreExplorer() => _explorerState.Restore();

    /// <summary>
    /// Called when a bar sees a new Explorer announce itself. Watches for Explorer saving the
    /// suppression as the user's auto-hide setting and puts it back as soon as it does, rather
    /// than only at exit. See <see cref="ExplorerStateGuard.WatchForLateSave"/>.
    /// </summary>
    /// <remarks>
    /// Every bar receives the broadcast, so with a bar on every monitor this is called once per
    /// bar; each call restarts the one watch, which is harmless.
    /// </remarks>
    public void OnExplorerRestarted() => _explorerState.WatchForLateSave(ExplorerStateGuard.LateSaveWindow);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RestoreExplorer();

        // Signals ManagedShell that AppBar teardown is expected, so it releases reserved space
        // rather than treating the disappearance as a crash to work around.
        ShellManager.AppBarManager.SignalGracefulShutdown();
        ShellManager.Dispose();
    }
}
