using ManagedShell;
using ManagedShell.Common.Enums;

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

    public ShellHost()
    {
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
            EnableTrayService = true,
            AutoStartTrayService = true,

            EnableWin11DpiWorkaround = false,
        };

        ShellManager = new ShellManager(config);

        _explorerState = new ExplorerStateGuard(new ExplorerTaskbarState(ShellManager.ExplorerHelper));
        _explorerState.Capture();

        // The load-bearing call. This sets Explorer's taskbar to autohide, which is what releases
        // its reserved work area — without it both bars reserve space and every maximised window
        // comes up short by two taskbar heights.
        ShellManager.ExplorerHelper.HideExplorerTaskbar = true;

        ShellManager.Tasks.Initialize();

        Diagnostics.BringUpLog.Write(
            $"tray before init: handle=0x{ShellManager.NotificationArea.Handle.ToInt64():X} " +
            $"failed={ShellManager.NotificationArea.IsFailed} " +
            $"icons={ShellManager.NotificationArea.TrayIcons.Count}");

        // Claims the tray and broadcasts TaskbarCreated, which is what makes every running
        // application re-add its notification icon.
        ShellManager.NotificationArea.Initialize();

        Diagnostics.BringUpLog.Write(
            $"tray after init: handle=0x{ShellManager.NotificationArea.Handle.ToInt64():X} " +
            $"failed={ShellManager.NotificationArea.IsFailed} " +
            $"icons={ShellManager.NotificationArea.TrayIcons.Count}");
    }

    public ShellManager ShellManager { get; }

    /// <summary>True when startup had to repair a previous run's unclean exit.</summary>
    public bool RecoveredFromUncleanExit => _explorerState.RecoveredFromUncleanExit;

    /// <summary>
    /// Puts Explorer back. Idempotent, and deliberately callable from an unhandled-exception
    /// handler where disposing the whole shell may not be safe.
    /// </summary>
    public void RestoreExplorer() => _explorerState.Restore();

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
