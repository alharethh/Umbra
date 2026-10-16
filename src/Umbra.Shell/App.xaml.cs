using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ManagedShell.AppBar;
using Umbra.Core.Layout;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Settings;
using Umbra.Shell.Shell;

namespace Umbra.Shell;

public partial class App : Application
{
    private ShellHost? _host;
    private BarWindow? _bar;

    /// <summary>Every bar, primary first. One entry unless BarOnEveryMonitor is on.</summary>
    private readonly List<BarWindow> _bars = [];
    private SingleInstance? _instance;
#if DEBUG
    private DevControl? _devControl;
#endif
    private string[] _args = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Explorer's taskbar is autohidden for as long as Umbra runs. Every path out of this
        // process has to put it back, including the ones that are not a clean Exit.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _host?.RestoreExplorer();

        _args = e.Args;
        BringUpLog.Write("--- startup ---");

        // A restart from the settings window hands us the outgoing process to wait on. Without
        // this the new bar can capture Explorer's taskbar state while the old one still has it
        // suppressed, then faithfully restore the user's taskbar to hidden on its own exit.
        bool restarting = WaitForPreviousInstance(e.Args);

        // One bar, and the check has to come before ShellHost: a second instance that got as far
        // as constructing one would capture Explorer's taskbar state while the first has it
        // suppressed, and then restore the user's taskbar to hidden when it exits. See
        // SingleInstance. A restart is given a grace period because it is racing its predecessor
        // out of the door on purpose; an ordinary launch waits for nothing.
        _instance = SingleInstance.Claim(timeout: restarting ? TimeSpan.FromSeconds(5) : TimeSpan.Zero);

        if (_instance is null)
        {
            // Silently. Double-clicking a taskbar and getting a dialog would be worse than
            // double-clicking it and getting nothing, which is what every other shell does.
            BringUpLog.Write("another Umbra already holds the bar; exiting without touching Explorer");
            Shutdown();
            return;
        }

        if (_instance.WasAbandoned)
            BringUpLog.Write("the single-instance lock was abandoned: the previous exit was not clean");

#if DEBUG
        // After the lock, so only the bar that is actually running listens. See DevControl.
        _devControl = new DevControl(Dispatcher, () =>
        {
            foreach (BarWindow bar in _bars.ToList()) bar.CloseForShutdown();
        });
#endif

        Settings = UmbraSettings.Load();

        // Beside the settings, because the single-file download has no files beside it.
        Shell.LicenceFiles.EnsureWritten(UmbraSettings.DefaultPath);
        ApplyCommandLineOverrides(Settings, e.Args);

        // <b>Re-point the sign-in entry if we have moved.</b> The standalone download is commonly
        // run out of the Downloads folder and then put somewhere permanent, which leaves the Run
        // value naming a file that is gone — and Windows drops such an entry without saying so,
        // so the symptom is "it stopped starting itself" with nothing to look at. Only touched
        // when the user has asked for it; Apply writes nothing if the path already matches.
        if (Settings.StartWithWindows) Shell.StartupRegistration.Apply(true);

        // Tray hosting works (docs/measurements.md step 7), but runs a permanent 100 ms timer,
        // and whether those timers defeat display idle is unmeasured -- so it waits to be asked for.
        _host = new ShellHost(enableTray: Settings.Tray);

        if (_host.RecoveredFromUncleanExit)
            BringUpLog.Write("recovered Explorer taskbar state from a previous unclean exit");

        // Bottom is the shipping default (§7.1) and the only edge that contends with Explorer's
        // autohidden taskbar (shell-integration §2.5). Left is the control: it is the one edge
        // where none of that contention exists, so running the two back to back is what makes the
        // contention attributable rather than merely observed.
        BarMetrics metrics = new(Settings.Edge, Settings.Alignment, scale: 1.0,
                                 baseIconExtent: Settings.DormantIconSize);

        foreach (AppBarScreen? screen in ScreensToOccupy(Settings))
        {
            bool primary = IsPrimary(screen);

            BarWindow bar = new(
                _host,
                metrics,
                screen,

                // Exactly one bar may host it. See BarWindow's constructor: ManagedShell's tray is
                // one collection fed by one hidden window, so a second row bound to it draws every
                // icon twice and forwards every click twice.
                hostsTray: primary,

                // One dataset per screen. The accumulator is sized to the reserved rectangle in
                // physical pixels, so two screens are two incompatible grids; sharing a file has
                // each bar refuse the other's data and then save over it.
                wearPath: Wear.WearSampler.PathForScreen(screen?.DeviceName, primary))
            {
                MotionEnabled = Settings.Motion,
            };

            // The hover colours, the row's hover treatment and where icons come from, before the
            // first paint — all ordinary drawn state rather than geometry, so they never go
            // through the placement path that ApplyLiveSettings would otherwise drag in at
            // startup.
            bar.ApplyHoverSettings(Settings);
            bar.ApplyIconSourceSetting(Settings);

            // Any bar closing ends the session. There is no way to close one from the UI, so this
            // only fires on shutdown — and a surviving bar with the others gone would be a worse
            // state than none, because Explorer's taskbar stays suppressed either way.
            bar.Closed += (_, _) => Shutdown();
            bar.Show();

            _bars.Add(bar);
            LogGeometry(metrics, bar, screen);
        }

        // Everything that reaches for "the bar" — settings, restart, the heatmap — means the one
        // the user is most likely looking at, which is the primary.
        _bar = _bars.Count > 0 ? _bars[0] : null;

        if (_bars.Count > 1)
            BringUpLog.Write($"bars on {_bars.Count} monitors; the tray is on the primary only");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        BringUpLog.Write("--- clean shutdown ---");

#if DEBUG
        _devControl?.Dispose();
        _devControl = null;
#endif

        _host?.Dispose();
        _host = null;

        // After the host, so the lock outlives Explorer's restore. A replacement waiting on this
        // lock must not start claiming the taskbar while we are still handing it back.
        _instance?.Dispose();
        _instance = null;

        base.OnExit(e);
    }

    /// <summary>
    /// Applies drawn-state settings to <em>every</em> bar.
    /// </summary>
    /// <remarks>
    /// The settings window is opened from whichever bar was right-clicked, and used to be handed
    /// that bar's own apply method. With a bar on each monitor that quietly restyles one screen
    /// and leaves the others as they were — the sort of bug that looks like the setting not
    /// working.
    /// </remarks>
    internal void ApplyLiveSettingsToAll(UmbraSettings settings)
    {
        foreach (BarWindow bar in _bars) bar.ApplyLiveSettings(settings);
    }

    /// <summary>The live settings, shared with the settings window.</summary>
    /// <remarks><c>App</c> is public because WPF requires it; the settings themselves are not.</remarks>
    internal UmbraSettings Settings { get; private set; } = new();

    /// <summary>
    /// Restarts Umbra so that edge, alignment, monitor or tray changes take effect.
    /// </summary>
    /// <remarks>
    /// The replacement is told to wait for this process to exit before it touches Explorer's
    /// taskbar state, because both instances briefly existing is exactly the race that leaves the
    /// user's taskbar hidden — see <c>ExplorerStateGuard</c>.
    /// </remarks>
    public void Restart()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            BringUpLog.Write("restart requested but the process path is unknown");
            return;
        }

        // Carry the original flags through, minus any previous wait token.
        List<string> args = ["--wait-for", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)];
        for (int i = 0; i < _args.Length; i++)
        {
            if (string.Equals(_args[i], "--wait-for", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            args.Add(_args[i]);
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                Arguments = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            });

            BringUpLog.Write("restarting");
            Shutdown();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            BringUpLog.Write(ex, "restart failed");
        }
    }

    /// <summary>Blocks until the process named by <c>--wait-for</c> has exited.</summary>
    /// <returns>True when a wait token was present, i.e. this launch is a restart.</returns>
    private static bool WaitForPreviousInstance(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--wait-for", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(args[i + 1], out int processId)) continue;

            try
            {
                using System.Diagnostics.Process previous = System.Diagnostics.Process.GetProcessById(processId);

                // Bounded: a hung predecessor must not leave the user with no taskbar at all.
                if (previous.WaitForExit(10_000)) BringUpLog.Write($"previous instance {processId} exited");
                else BringUpLog.Write($"previous instance {processId} did not exit within 10s; continuing");
            }
            catch (ArgumentException)
            {
                // Already gone, which is the outcome we wanted.
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the configured monitor, falling back to the primary when it is no longer attached.
    /// </summary>
    /// <summary>
    /// Which screens get a bar: every one, or just the chosen one.
    /// </summary>
    /// <remarks>
    /// <b>Primary first.</b> The order decides which bar owns the tray and which one the settings
    /// window and heatmap open against, and "the first screen Windows enumerated" is not a
    /// property anybody chose — on a machine with a virtual adapter it is not even stable.
    /// </remarks>
    private static IReadOnlyList<AppBarScreen?> ScreensToOccupy(UmbraSettings settings)
    {
        if (!settings.BarOnEveryMonitor) return [ResolveScreen(settings.Monitor)];

        try
        {
            List<AppBarScreen> all = [.. AppBarScreen.FromAllScreens()];
            if (all.Count == 0) return [null];

            all.Sort((a, b) => IsPrimary(b).CompareTo(IsPrimary(a)));

            return [.. all.Cast<AppBarScreen?>()];
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            BringUpLog.Write(e, "could not enumerate screens; falling back to one bar");
            return [ResolveScreen(settings.Monitor)];
        }
    }

    /// <summary>Whether a screen is the primary one. A null screen means the primary.</summary>
    private static bool IsPrimary(AppBarScreen? screen)
    {
        if (screen is null) return true;

        try
        {
            return string.Equals(
                screen.DeviceName,
                AppBarScreen.FromPrimaryScreen().DeviceName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            return true;
        }
    }

    private static AppBarScreen? ResolveScreen(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;

        try
        {
            foreach (AppBarScreen screen in AppBarScreen.FromAllScreens())
                if (string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    return screen;

            BringUpLog.Write($"configured monitor '{deviceName}' is not attached; using the primary");
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            BringUpLog.Write(e, "could not enumerate monitors; using the primary");
        }

        return null;
    }

    /// <summary>
    /// Lets command-line flags win over the stored settings.
    /// </summary>
    /// <remarks>
    /// The flags exist for experiments — <c>--no-motion</c> is there so the §11.2 Q1 comparison
    /// needs no rebuild — and an experiment silently losing to a stored setting would be worse than
    /// having no flag at all.
    /// </remarks>
    private static void ApplyCommandLineOverrides(UmbraSettings settings, string[] args)
    {
        if (ParseEdgeOverride(args) is { } edge) settings.Edge = edge;
        if (ParseIconSize(args) is { } size) settings.DormantIconSize = size;
        if (args.Contains("--no-motion", StringComparer.OrdinalIgnoreCase)) settings.Motion = false;
        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) settings.Tray = true;
    }

    /// <summary>
    /// <c>--icon-size N</c>, in pixels at 100% scaling. Null keeps the §7.1 default of 24.
    /// </summary>
    /// <remarks>
    /// Clamped to the slot: an icon wider than its pitch would throw, and one at the pitch leaves
    /// no gutter for drift at all.
    /// </remarks>
    private static double? ParseIconSize(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--icon-size", StringComparison.OrdinalIgnoreCase)) continue;

            if (double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                out double parsed)
                && parsed > 0 && parsed <= Umbra.Core.Layout.SlotMetrics.BasePitch)
            {
                BringUpLog.Write($"icon size overridden to {parsed} px (default is 24)");
                return parsed;
            }

            BringUpLog.Write($"unusable --icon-size value '{args[i + 1]}', keeping the default");
        }

        return null;
    }

    /// <summary>
    /// <c>--edge left|top|right|bottom</c>. Defaults to bottom, the shipping default (§7.1).
    /// </summary>
    private static BarEdge? ParseEdgeOverride(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--edge", StringComparison.OrdinalIgnoreCase)) continue;
            if (UmbraSettings.TryParseName(args[i + 1], out BarEdge parsed)) return parsed;

            BringUpLog.Write($"unrecognised --edge value '{args[i + 1]}', keeping the stored edge");
        }

        return null;
    }

    /// <summary>
    /// Logs what the spec model computed against what the AppBar actually got.
    /// </summary>
    /// <remarks>
    /// The expected rectangle is computed against <paramref name="screen"/>, not the primary.
    /// Taking it from <c>SystemParameters.PrimaryScreen*</c> made this comparison disagree with
    /// itself on every run that placed the bar on a secondary monitor — an instrument that always
    /// reports a mismatch is as useless as one that can never report a pass.
    /// </remarks>
    private static void LogGeometry(BarMetrics metrics, BarWindow bar, AppBarScreen? screen)
    {
        // AppBarScreen reports physical pixels; the model works in DIP, which is what the bar's
        // own Left/Top/Width/Height are in.
        double scale = bar.DpiScale <= 0 ? 1.0 : bar.DpiScale;

        double width = screen is null ? SystemParameters.PrimaryScreenWidth : screen.Bounds.Width / scale;
        double height = screen is null ? SystemParameters.PrimaryScreenHeight : screen.Bounds.Height / scale;
        double originX = screen is null ? 0 : screen.Bounds.X / scale;
        double originY = screen is null ? 0 : screen.Bounds.Y / scale;

        Umbra.Core.Layout.Rect reserved = metrics.ReservedRect(width, height);

        BringUpLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"edge={metrics.Edge} alignment={metrics.Alignment} dpiScale={bar.DpiScale:F2}"));

        BringUpLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"reserved extent={metrics.ReservedExtent} band={metrics.ContentBand} " +
            $"pitch={metrics.Slot.Pitch} icon={metrics.Slot.IconExtent} rowInset={metrics.RowInset:F2}"));

        // What the spec model computed against what the AppBar actually got. A mismatch here is
        // the first thing worth knowing at step 1, and it is invisible from the core library's
        // own tests.
        BringUpLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"expected rect  x={reserved.X + originX} y={reserved.Y + originY} " +
            $"w={reserved.Width} h={reserved.Height}"));

        BringUpLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"actual   rect  x={bar.Left} y={bar.Top} w={bar.Width} h={bar.Height}"));

        BringUpLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"work area after reservation: {SystemParameters.WorkArea}"));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        BringUpLog.Write(e.Exception, "unhandled dispatcher exception");
        _host?.RestoreExplorer();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            BringUpLog.Write(exception, "unhandled domain exception");

        _host?.RestoreExplorer();
    }
}
