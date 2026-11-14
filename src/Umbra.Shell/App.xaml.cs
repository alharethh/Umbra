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

    /// <summary>The screens a bar-per-monitor set was built for. Empty for a lone bar.</summary>
    private IReadOnlyList<Display> _builtFor = [];

    /// <summary>Waits for a display change to settle before judging it. See OnDisplaySettingsChanged.</summary>
    private DispatcherTimer? _topologySettle;

    /// <summary>When the last display change arrived, on <see cref="Environment.TickCount64"/>.</summary>
    private long _lastDisplayChange;
#if DEBUG
    private DevControl? _devControl;
    private DevFaults? _devFaults;
#endif
    private string[] _args = [];
    private SettingsRequest? _settingsRequest;
    private Settings.SettingsWindow? _settingsWindow;

    /// <summary>
    /// Opens the settings window, or brings the open one to the front.
    /// </summary>
    /// <remarks>
    /// One window, wherever it is asked for from — the bar's menu or a second launch. Two settings
    /// windows editing two copies of the same settings would each save over the other.
    /// <c>ShowInFront</c>, not <c>Show</c>: the bar never holds the foreground, so a window it
    /// opens lands behind whatever the user was looking at. See <c>WindowOrder</c>.
    /// </remarks>
    internal void OpenSettings()
    {
        if (_settingsWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.ShowInFront();
            return;
        }

        // ApplyLiveSettingsToAll: with a bar per monitor, every bar must change, not only the one
        // the window was opened from.
        _settingsWindow = new Settings.SettingsWindow(Settings, ApplyLiveSettingsToAll, Restart);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.ShowInFront();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Explorer's taskbar is autohidden for as long as Umbra runs. Every path out of this
        // process has to put it back, including the ones that are not a clean Exit.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _host?.RestoreExplorer();

        // Not fatal on .NET, and the bar is right to carry on: measured with a Debug fault hook, an
        // unobserved task left a working bar with the taskbar correctly suppressed. Logged, because
        // otherwise it is the one fault that leaves no trace at all.
        TaskScheduler.UnobservedTaskException += (_, args) =>
            BringUpLog.Write(args.Exception, "unobserved task exception; the bar carries on");

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
            // No dialog: the running bar opens its settings instead, which is the useful answer
            // to someone starting Umbra again. Still without touching Explorer. See SettingsRequest.
            if (SettingsRequest.ShouldSignal(restarting))
                SettingsRequest.LogOutcome(SettingsRequest.Signal());
            else
                BringUpLog.Write("another Umbra already holds the bar; exiting without touching Explorer");

            Shutdown();
            return;
        }

        if (_instance.WasAbandoned)
            BringUpLog.Write("the single-instance lock was abandoned: the previous exit was not clean");

        // After the lock, so only the bar that is actually running listens.
        _settingsRequest = new SettingsRequest(() => Dispatcher.BeginInvoke(() =>
        {
            BringUpLog.Write("settings: opened because Umbra was started again");
            OpenSettings();
        }));

#if DEBUG
        // After the lock, so only the bar that is actually running listens. See DevControl.
        _devControl = new DevControl(Dispatcher, () =>
        {
            foreach (BarWindow bar in _bars.ToList()) bar.CloseForShutdown();
        });
        _devFaults = new DevFaults(Dispatcher);
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

        // After the host, because the marker file it just wrote is what the guard acts on. Covers
        // every death no handler in this process can see: a kill, a native crash, FailFast, a
        // stack overflow, "close the program" on a hung window. See CrashGuard.
        CrashGuard.Launch();

#if DEBUG
        // After the guard, with Explorer suppressed and no bar drawn: the worst moment to fail.
        if (DevFaults.StartupRequested(e.Args)) DevFaults.ThrowStartupFault();
#endif

        // Bottom is the shipping default (§7.1) and the only edge that contends with Explorer's
        // autohidden taskbar (shell-integration §2.5). Left is the control: it is the one edge
        // where none of that contention exists, so running the two back to back is what makes the
        // contention attributable rather than merely observed.
        BarMetrics metrics = new(Settings.Edge, Settings.Alignment, scale: 1.0,
                                 baseIconExtent: Settings.DormantIconSize);

        IReadOnlyList<AppBarScreen?> screens = ScreensToOccupy(Settings);

        // Before any bar opens its wear file: the first launch after per-placement files gives
        // each screen's old single record to the edge it was recorded at, once, for the whole set.
        // See WearSampler.AdoptLegacyRecords.
        foreach (string line in Wear.WearSampler.AdoptLegacyRecords(
                     screens.Select(s => (
                         Wear.WearSampler.PathForScreen(s?.DeviceName, IsPrimary(s)),
                         Wear.WearSampler.PathFor(s?.DeviceName, IsPrimary(s), Settings.Edge))),
                     Wear.WearSampler.LegacyMarkerPath))
            BringUpLog.Write(line);

        foreach (AppBarScreen? screen in screens)
        {
            bool primary = IsPrimary(screen);

            BarWindow bar = new(
                _host,
                metrics,
                screen,

                // Exactly one bar may host it. See BarWindow's constructor: ManagedShell's tray is
                // one collection fed by one hidden window, so a second row bound to it draws every
                // icon twice and forwards every click twice.
                // Its wear dataset is not passed in: the bar picks it from its screen and edge
                // (WearSampler.PathFor), so the choice follows a live move.
                hostsTray: primary)
            {
                MotionEnabled = Settings.Motion,

                // Each bar of a per-monitor set keeps its own screen through a live settings
                // change; the monitor setting applies only to a lone bar. See PlacementScreen.
                OwnsItsScreen = Settings.BarOnEveryMonitor,
            };

            // The hover colours, the row's hover treatment and where icons come from, before the
            // first paint — all ordinary drawn state rather than geometry, so they never go
            // through the placement path that ApplyLiveSettings would otherwise drag in at
            // startup.
            bar.ApplyHoverSettings(Settings);
            bar.ApplyIconSourceSetting(Settings);
            bar.ApplyRestingBrightness(Settings);
            bar.ApplyAwaySetting(Settings);

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

        // A fullscreen application already in front at launch would otherwise be drawn over until
        // the user switched windows. Once the bars have registered, so they hear the answer.
        FullScreenHelper fullScreen = _host.ShellManager.FullScreenHelper;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => FullScreenRecheck.Run(fullScreen));

        // Recorded here, not read back off the bars later: ManagedShell rewrites the old primary
        // bar's screen during the very display change that has to be judged. See MonitorTopology.
        if (Settings.BarOnEveryMonitor)
        {
            _builtFor = [.. screens.OfType<AppBarScreen>().Select(s => new Display(s.DeviceName, IsPrimary(s)))];
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            BringUpLog.Write($"bar set built for {MonitorTopology.Describe(_builtFor)}");

            // The screens were enumerated before the bars were built and shown, and a monitor that
            // arrived in between raised no event this process heard. That is exactly where a
            // restart during a flapping monitor lands, so judge once as though a change had come
            // in at launch.
            NoteDisplayChange();
        }
    }

    /// <summary>
    /// A monitor was added or removed, the primary changed, or a mode changed. Judged once the
    /// change has settled, because plugging a monitor in raises a burst of these and an
    /// enumeration in the middle of one can see half a desktop.
    /// </summary>
    /// <remarks>
    /// Marshalled: <c>SystemEvents</c> may raise this on its own thread.
    /// </remarks>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(NoteDisplayChange);

    /// <summary>
    /// Records a display change and judges it once the displays have been quiet for
    /// <see cref="MonitorTopology.SettleDelay"/>. Restarted on every change, so the judgement comes
    /// after the last one.
    /// </summary>
    private void NoteDisplayChange()
    {
        _lastDisplayChange = Environment.TickCount64;
        JudgeTopologyIn(MonitorTopology.SettleDelay);
    }

    private void JudgeTopologyIn(TimeSpan delay)
    {
        _topologySettle ??= new DispatcherTimer(
            delay, DispatcherPriority.Normal, (_, _) => OnTopologySettled(), Dispatcher);

        _topologySettle.Stop();
        _topologySettle.Interval = delay;
        _topologySettle.Start();
    }

    /// <summary>
    /// Rebuilds the bar set by restarting, if the monitors it was built for are not the ones
    /// attached any more.
    /// </summary>
    /// <remarks>
    /// <b>A restart rather than an in-place rebuild</b>, because what a rebuild has to change is
    /// fixed at construction: which bar hosts the tray (one collection, one bar, on the primary),
    /// which wear dataset each bar keeps, and the rule that any bar closing ends the session. A
    /// restart builds the set exactly as launch does, through the path that already guards
    /// Explorer's taskbar state across the handover.
    /// </remarks>
    private void OnTopologySettled()
    {
        _topologySettle?.Stop();

        IReadOnlyList<Display> attached;
        try
        {
            attached = [.. AppBarScreen.FromAllScreens().Select(s => new Display(s.DeviceName, IsPrimary(s)))];
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            BringUpLog.Write(ex, "display change: could not enumerate screens; bars left in place");
            return;
        }

        TopologyChange change = MonitorTopology.Classify(perMonitorSet: true, _builtFor, attached);
        if (change == TopologyChange.None)
        {
            BringUpLog.Write($"display change: still {MonitorTopology.Describe(attached)}; bars left in place");
            return;
        }

        string what = $"built for {MonitorTopology.Describe(_builtFor)}, now {MonitorTopology.Describe(attached)}";

        TimeSpan quietFor = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastDisplayChange);
        TimeSpan settle = MonitorTopology.StillToSettle(change, quietFor);
        if (settle > TimeSpan.Zero)
        {
            BringUpLog.Write($"display change: {what}; only removed, judged again in {settle.TotalSeconds:0}s");
            JudgeTopologyIn(settle);
            return;
        }

        // Recorded before the attempt, so a restart that fails counts too and cannot be retried on
        // every display change either. A record that cannot be read or written refuses the
        // rebuild: the next process would not see it, and the limit is all that stops a flap.
        RebuildClaim claim = MonitorTopology.ClaimRebuild(MonitorTopology.DefaultRebuildStampPath, DateTime.UtcNow, out TimeSpan wait);
        if (claim != RebuildClaim.Granted)
        {
            // Judged again when the limit allows, so a real change that lands at the end of a
            // burst is still followed, once, rather than waiting for the next display change.
            string why = claim switch
            {
                RebuildClaim.Limited => "rate limit",
                RebuildClaim.Unreadable => "the rebuild record cannot be read",
                _ => "the rebuild record cannot be written",
            };
            BringUpLog.Write($"display change: {what}; rebuild deferred ({why}), judged again in {wait.TotalSeconds:0}s");
            JudgeTopologyIn(wait);
            return;
        }

        BringUpLog.Write($"display change: {what}; restarting to rebuild the bar set");

        // Only once the replacement has started: a restart that failed leaves this bar running,
        // and its set still has to follow the next change.
        if (TryRestart())
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        BringUpLog.Write("--- clean shutdown ---");

        // First, before anything below can stall: a bar the hang watch ends on its way out must
        // not be relaunched by the guard. Exit and Restart have declared already; this catches
        // every other way to Shutdown. See ExitIntent.
        ExitIntent.Declare("exit");

        _settingsRequest?.Dispose();
        _settingsRequest = null;

#if DEBUG
        _devControl?.Dispose();
        _devControl = null;
        _devFaults?.Dispose();
        _devFaults = null;
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
    /// Logoff or shutdown. Declared here rather than left to <see cref="OnExit"/>, because a
    /// session that is ending can stall the bar long before WPF gets as far as exiting, and a bar
    /// relaunched into a closing session would be ended again by Windows anyway, after suppressing
    /// Explorer's taskbar on the way.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ExitIntent.Declare($"session ending ({e.ReasonSessionEnding})");
        base.OnSessionEnding(e);
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
    public void Restart() => TryRestart();

    /// <summary>Starts a replacement and shuts this bar down. False if the replacement could not be started.</summary>
    private bool TryRestart()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            BringUpLog.Write("restart requested but the process path is unknown");
            return false;
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

            // Once the replacement is started, not before: a restart that failed leaves this bar
            // running, and a real hang later must still be relaunched. From here on a stall on the
            // way out must not have the guard start a third bar against the replacement.
            ExitIntent.Declare("restart");
            BringUpLog.Write("restarting");
            Shutdown();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            BringUpLog.Write(ex, "restart failed");
            return false;
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
    internal static bool IsPrimary(AppBarScreen? screen)
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
    internal static BarEdge? ParseEdgeOverride(string[] args)
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
