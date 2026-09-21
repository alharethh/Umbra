using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedShell.AppBar;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using Umbra.Core.Layout;
using Umbra.Core.Motion;
using Umbra.Core.Tiers;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.JumpList;
using Umbra.Shell.Motion;
using Umbra.Shell.Picker;
using Umbra.Shell.Rendering;
using Umbra.Shell.Shell;
using Umbra.Shell.Tasks;
using Umbra.Shell.Tiers;
using Umbra.Shell.Wear;
using CoreBarEdge = Umbra.Core.Layout.BarEdge;

namespace Umbra.Shell;

/// <summary>
/// The bar. Build order step 1: renders, reserves space, lists live windows.
/// </summary>
/// <remarks>
/// <para>
/// Every number here comes from <see cref="BarMetrics"/> rather than from a literal, which is what
/// makes the core library's geometry answerable at runtime rather than only in its own tests.
/// </para>
/// <para>
/// <b>The metrics are built at scale 1.0 deliberately.</b> WPF lays out in device-independent
/// pixels, so the spec's 100%-scaling column <i>is</i> the DIP column at every DPI — 44 DIP pitch
/// renders as 55 physical pixels at 125% without anything here knowing about it. The scale
/// parameter on <see cref="BarMetrics"/> is for the places that genuinely count physical pixels,
/// which so far is only the wear accumulator.
/// </para>
/// </remarks>
internal partial class BarWindow : AppBarWindow
{
    // AppBarWindow already implements INotifyPropertyChanged and exposes OnPropertyChanged, so the
    // clock's plain properties notify through the base class rather than a second implementation.

    /// <summary>
    /// Whether the motion layer runs. The experiment in §11.2 Q1 is a week with drift on against a
    /// week with it off, compared on the heatmaps, so switching it must not need a rebuild.
    /// </summary>
    public bool MotionEnabled { get; init; } = true;

    /// <param name="screen">
    /// Which monitor to occupy. Fixed for the window's lifetime: it is part of the space-reserving
    /// AppBar registration, and §6.3 forbids re-registering that in steady state.
    /// </param>
    public BarWindow(ShellHost host, BarMetrics metrics, AppBarScreen? screen = null)
        : base(
            host.ShellManager.AppBarManager,
            host.ShellManager.ExplorerHelper,
            host.ShellManager.FullScreenHelper,
            screen ?? AppBarScreen.FromPrimaryScreen(),
            ToAppBarEdge(metrics.Edge),

            // Never AppBarMode.AutoHide. Windows allows one autohide AppBar per screen edge and
            // Explorer's taskbar holds that slot as a consequence of how it is suppressed; asking
            // for it produces "a toolbar is already hidden on that side of the screen"
            // (shell-integration §2.4). Tier 0 is a reserved bar painted black, not a hidden one.
            AppBarMode.Normal,

            // Depth of the reserved rectangle across the bar's short axis: 64 DIP on a vertical
            // bar, 56 on a horizontal one. ReservedExtent already resolves that by edge.
            metrics.ReservedExtent)
    {
        Metrics = metrics;
        Windows = host.ShellManager.Tasks.GroupedWindows;

        _tasks = new TaskbarModel(Windows, Pins);

        // Null when tray hosting is switched off — see ShellHost.
        //
        // Two collections now that §7.1's overflow chevron exists: the system indicators stay on
        // the bar, everything else lives behind the chevron. ManagedShell maintains both views off
        // the pinned-GUID list ShellHost supplies, so nothing here has to filter.
        _notificationArea = host.ShellManager.NotificationArea;
        TrayIcons = _notificationArea?.PinnedIcons;
        OverflowTrayIcons = _notificationArea?.UnpinnedIcons;

        if (_notificationArea is not null)
            _notificationArea.TrayIcons.CollectionChanged += (_, _) => OnTrayIconsChanged();

        DataContext = this;
        InitializeComponent();

        // Placed by callback rather than by a PlacementMode, because none of them centres. See
        // FlyoutPlacement: Top hangs the panel off the chevron's left edge, which runs it off the
        // end of the screen from a chevron that sits near the trailing end of the bar.
        TrayOverflowPopup.CustomPopupPlacementCallback = PlaceTrayOverflow;

        // Zero, not §3's 40 px: the bar engages when the pointer is on it, not when the pointer is
        // near it. A 40 px band lights the bar up on the way past, which reads as the taskbar
        // reacting to traffic rather than to the user. The Tier 0 → 1 ambient ramp is untouched —
        // it is a near-invisible grey step and it is what §2.3's duty budget is built on.
        _proximity = new ProximityDriver(
            () => new System.Windows.Rect(Left, Top, Width, Height),
            () => DpiScale <= 0 ? 1.0 : DpiScale,
            engagedBandPixels: 0.0);

        _proximity.LevelChanged += level => TierLevel = level;

        // A panel of ours is part of the bar as far as the tier is concerned. Without this,
        // reaching into the overflow flyout takes the pointer off the bar, the level ramps down,
        // and the icons grey out underneath the panel being used.
        _proximity.PointerOverPanel = PointerIsOnOwnPanel;

        // Polled from the proximity tick rather than its own timer — §6.6 is about not adding
        // wake-ups, and this needs no better resolution than the tier ramp does.
        //
        // The tick is authoritative, and deliberately so. Setting the flag on click and leaving the
        // watcher to clear it means the glyph stays coloured forever if the menu never opens — and
        // it can fail to open: ShowStartMenu is a synthesised Win keypress, which does nothing when
        // a higher-integrity window has focus (§7.0). Recomputing the whole condition every tick
        // cannot get stuck.
        _proximity.Ticked += () =>
        {
            _startMenu.Poll();

            // A tray icon's menu belongs to the application that owns the icon, and clicking the
            // bar does not close it for the same reason nothing else does — the foreground never
            // moves. Polled here rather than handled on a mouse event, because a Win32 menu may
            // hold the mouse capture, in which case the press never reaches us at all.
            // Guarded like every other use of DpiScale in this file. Unguarded it can be 0 before
            // the window has a source, which collapses the rectangle and silently stops the poll.
            double dpi = DpiScale <= 0 ? 1.0 : DpiScale;

            _foreignMenus.Poll(new System.Windows.Rect(
                Left * dpi, Top * dpi, Width * dpi, Height * dpi));

            bool justPressed = DateTime.UtcNow - _startPressedAt < StartPressGrace;
            StartActive = _startMenu.IsOpen || justPressed;
            StartHighlighted = StartActive || StartButton.IsMouseOver;

            // Which slot owns the foreground window, for the running indicator — and, from the
            // same handle, which input language the next keystroke will be typed in.
            IntPtr foreground = GetForegroundWindow();
            _tasks.UpdateForeground(foreground);

            // Not while Start is up: it is a surface of its own, with a layout of its own, and
            // the indicator should still be naming the application underneath it.
            if (!_startMenu.IsOpen && _language.Poll(foreground))
            {
                OnPropertyChanged(nameof(LanguageName));
                OnPropertyChanged(nameof(LanguageVisibility));
            }

            // Keep polling at the fast interval while the menu is up, even if the pointer has
            // wandered off the bar. Otherwise the tick drops to 100 ms and the colour outlives the
            // menu by up to a tenth of a second, which is long enough to look like lag.
            _proximity.KeepFast = StartActive;
        };

        OnClockTick(null, EventArgs.Empty);
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += OnClockTick;

        // Before the menu is placed, not after: dismissing Start once the popup is already up
        // would show it clipped for as long as Start takes to animate away. handledEventsToo,
        // because the slot handler runs first and this must not depend on what it does.
        AddHandler(
            ContextMenuOpeningEvent,
            new ContextMenuEventHandler((_, _) =>
            {
                ClearPanelsFor(BarPanel.ContextMenu);
                DismissWindowsSurfaces();
            }),
            handledEventsToo: true);

        // A press anywhere on the bar closes an open menu, before anything else looks at it.
        //
        // The pointer watcher in ContextMenuDismisser was supposed to cover this and does not:
        // reported as "clicking off the menu within the bar still doesn't close the context menu",
        // while clicking outside Umbra closes it fine. So the press that lands on the bar is not
        // reaching that poll, and the bar is the one surface where the user is most likely to
        // click next. Handling it here needs no theory about why — the press is routed to us, and
        // PreviewMouseDown with handledEventsToo sees it before any slot handler can mark it
        // handled or open something else over the top.
        AddHandler(
            PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnBarPressed),
            handledEventsToo: true);

        // A class handler rather than per-menu wiring: the slot menus are created by a
        // DataTemplate, so there is no single place to attach to, and a menu added later would
        // silently miss out.
        EventManager.RegisterClassHandler(
            typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is not ContextMenu menu) return;

                _menuDismisser.Watch(menu);

                // The popup is its own top-level window and does not inherit the bar's topmost
                // flag, so without this it draws under the Start menu. Opened, not Opening: the
                // popup has no HWND to reorder until it has one.
                WindowOrder.Raise(menu);
            }));

        EventManager.RegisterClassHandler(
            typeof(ContextMenu), ContextMenu.ClosedEvent,
            new RoutedEventHandler((_, _) => _menuDismisser.Release()));

        // Raised on a thread-pool thread by NetworkChange, so it is marshalled rather than
        // touching a dependency property from wherever the network stack happened to notify.
        _network.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(NetworkGlyph));
            OnPropertyChanged(nameof(NetworkDescription));
        });

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _network.Dispose();
            _menuDismisser.Release();
            _clock.Stop();
            _proximity.Dispose();
            _motion?.Dispose();
            _wear?.Dispose();

            // A picker outlives the bar otherwise: it is a separate top-level window, and its DWM
            // thumbnail registrations would leak with it.
            _picker?.Dismiss();
            _picker = null;
        };
    }

    private readonly ProximityDriver _proximity;
    private readonly NotificationArea? _notificationArea;
    private readonly DispatcherTimer _clock;
    private readonly StartMenuWatcher _startMenu = new();
    private readonly KeyboardLayoutWatcher _language = new();
    private readonly NetworkWatcher _network = new();
    private readonly TaskbarModel _tasks;

    /// <summary>
    /// How long the glyph stays coloured after a press, independently of the menu.
    /// </summary>
    /// <remarks>
    /// Covers the gap between the click and the menu taking the foreground, and bounds the failure
    /// when the menu never appears at all. Slightly longer than the press animation, so the colour
    /// does not drop out mid-spring.
    /// </remarks>
    private static readonly TimeSpan StartPressGrace = TimeSpan.FromMilliseconds(450);

    private DateTime _startPressedAt = DateTime.MinValue;
    private MotionDriver? _motion;
    private WindowPicker? _picker;

    // Hover preview state. The timer exists for the whole life of the window but only runs while a
    // pointer is resting on a slot.
    private readonly ContextMenuDismisser _menuDismisser = new();
    private readonly JumpListIndex _jumpLists = new();
    private DispatcherTimer? _hoverTimer;
    private TaskbarSlot? _hoverSlot;
    private FrameworkElement? _hoverElement;

    /// <summary>Dormant icon extent in DIP; 0 means "whatever the metrics were built with".</summary>
    private double _dormantIconExtent;

    // Drag-reorder state. Null slot means no press is being tracked.
    private TaskbarSlot? _pressedSlot;
    private FrameworkElement? _pressedElement;
    private Point _pressedAt;
    private bool _dragging;
    private UIElement? _dragContainer;
    private double _grabAlong;
    private SlidingStackPanel? _row;
    private WearSampler? _wear;

    /// <summary>
    /// The drift and micro-jitter offset, applied to the drawn content of every slot.
    /// </summary>
    /// <remarks>
    /// One shared transform, so the whole content group moves coherently and icons never reorder or
    /// move relative to each other (§6.2). It is applied <b>inside</b> each hit slot rather than to
    /// the row, so the click targets stay exactly where the undrifted layout put them (P5, §6.5).
    /// </remarks>
    public TranslateTransform ContentOffset { get; } = new();

    /// <summary>
    /// The running indicator's offset: the full drift along the bar, clamped across it.
    /// </summary>
    /// <remarks>
    /// Its own transform source rather than <see cref="ContentOffset"/>, because the pill is the
    /// only drifting element that is not centred in the content band. See
    /// <see cref="ClampPillOffset"/> for why that matters and <see cref="PillEdgeClearance"/> for
    /// how much room it actually has.
    /// </remarks>
    public TranslateTransform PillOffset { get; } = new();

    /// <summary>
    /// How far the running-indicator pill sits from the outer edge of the bar, in DIP.
    /// </summary>
    /// <remarks>
    /// Windows 11 puts it hard against the edge, and this is the whole of its short-axis clearance.
    /// <see cref="PillMargin"/> is built from it, so the drawn margin and the clamp cannot disagree.
    /// </remarks>
    public const double PillEdgeClearance = 1.0;

    /// <summary>
    /// Holds the pill's short-axis drift inside the clearance it actually has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other drifting element is centred in the content band, so the band absorbs the
    /// short-axis travel: a 16 px icon in a 48 px band has 16 px of clearance against a peak
    /// excursion of about 8.75 px. The pill is pinned to the outer edge with
    /// <see cref="PillEdgeClearance"/> px, and 8.75 px of travel took it off the window, where it
    /// was clipped — partly for most of the drift cycle and entirely at the extremes. The symptom
    /// was a running indicator that was sometimes drawn wrong and sometimes not drawn at all, with
    /// no pattern anyone could see, because the drift period is minutes long.
    /// </para>
    /// <para>
    /// <b>This became reachable when the reserved rectangle was cut from 56 to 48.</b> The 8 px it
    /// gave up were the headroom that used to absorb exactly this, and the reasoning recorded for
    /// that change weighed the icon, which is centred and had clearance to spare, rather than the
    /// one element that had none.
    /// </para>
    /// <para>
    /// Clamped, not zeroed. The long axis is untouched and is the half that does the work anyway:
    /// the pill is 16 px long and 3 px thick, so travel along the bar spreads its wear over five
    /// times the distance travel across it ever could.
    /// </para>
    /// </remarks>
    public static double ClampPillOffset(double offset, double clearance) =>
        Math.Clamp(offset, -clearance, clearance);

    /// <summary>
    /// How long the pointer must rest on an icon before its preview appears, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Long enough that sweeping the row on the way to somewhere else spawns nothing, short enough
    /// that deliberately pointing at an icon feels answered rather than delayed. The Windows
    /// taskbar sits in the same region.
    /// </remarks>
    private const int HoverPreviewDelayMilliseconds = 450;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(HoverPreviewDelayMilliseconds),
        };

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer!.Stop();
            if (_hoverSlot is not null && _hoverElement is not null) ShowPreview(_hoverSlot, _hoverElement);
        };

        _proximity.Start();
        _clock.Start();
        SuppressActivation();
        LogWindowInventoryOnce();

        // Built here rather than in the constructor because the accumulator is sized in physical
        // pixels and the window has no size until it has been laid out.
        StartWear();

        MeasureClock();

        if (_notificationArea is not null)
        {
            LogTrayContents("at load");
            _notificationArea.TrayIcons.CollectionChanged += (_, _) => LogTrayContents("changed");
        }

        if (MotionEnabled)
        {
            _motion = new MotionDriver(() => CurrentMetrics);

            // The bar's own ratio, not the spec's: the tray is drawn at 20-in-28 rather than
            // §7.1's 16-in-24. See TrayPitchExtent for why the two have to move together.
            double trayRatio = TrayDriftRatio;

            _motion.OffsetChanged += (dx, dy) =>
            {
                ContentOffset.X = dx;
                ContentOffset.Y = dy;

                // The pill is the one drifting element that is not centred in the content band, so
                // it is the one the band's clearance does not protect. See ClampPillOffset.
                PillOffset.X = IsVertical ? ClampPillOffset(dx, PillEdgeClearance) : dx;
                PillOffset.Y = IsVertical ? dy : ClampPillOffset(dy, PillEdgeClearance);

                TrayOffset.X = dx * trayRatio;
                TrayOffset.Y = dy * trayRatio;

                // The network glyph sits among the tray icons and is the same size, so it takes
                // the tray's amplitude rather than the row's — its slot is no wider than theirs,
                // and P5 is per slot.
                NetworkOffset.X = dx * trayRatio;
                NetworkOffset.Y = dy * trayRatio;
            };

            _motion.ClockOffsetChanged += (dx, dy) =>
            {
                ClockOffset.X = dx;
                ClockOffset.Y = dy;

                // The same numbers into a second transform, because the language indicator is text
                // beside the clock and wants the clock's treatment — but never the clock's
                // transform object. See LanguageOffset.
                LanguageOffset.X = dx;
                LanguageOffset.Y = dy;
            };
            _motion.Start();
        }
        else
        {
            BringUpLog.Write("motion disabled by settings (or --no-motion)");
        }

        BringUpLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"motion: drift +/-{Metrics.Slot.DriftAmplitude:F2} px long axis, " +
            $"+/-{Metrics.ShortAxisDriftAmplitude:F2} px short axis, " +
            $"micro +/-{Metrics.Slot.MicroAmplitude:F2} px at {MotionModel.MicroUpdateRateHz} Hz, " +
            $"peak {Metrics.Slot.PeakOffset:F2} px against a {Metrics.Slot.Gutter:F2} px gutter"));
    }

    /// <summary>
    /// Logs every window the bar believes it owns, with its class, once at startup.
    /// </summary>
    /// <remarks>
    /// This exists because of the File Explorer close crash. The close path itself is ordinary —
    /// a <c>WM_CLOSE</c> to a window handle — so the only thing that can make it catastrophic is
    /// the handle being a shell window rather than a browser window. That is not something to
    /// reason about from the outside: <c>explorer.exe</c> owns the desktop, the taskbar and every
    /// File Explorer window, and they all group into one slot under
    /// <see cref="AppIdentity"/>. Printing the classes answers it directly, and costs one pass
    /// over a handful of windows at startup.
    /// </remarks>
    private void LogWindowInventoryOnce()
    {
        // Deferred: the tasks service is still enumerating when the bar first loads, so an
        // immediate pass would report a list that is not the one the user will click on.
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            BringUpLog.Write("--- window inventory ---");

            foreach (TaskbarSlot slot in _tasks.Slots)
            {
                BringUpLog.Write($"slot {slot.Key} | {slot.Windows.Count} window(s)");

                foreach (ApplicationWindow window in slot.Windows)
                {
                    bool closeable = WindowSafety.CanClose(window.Handle);
                    BringUpLog.Write(
                        $"    {(closeable ? "ok     " : "REFUSED")} {WindowSafety.Describe(window.Handle)}");
                }
            }

            BringUpLog.Write("--- end inventory ---");
        };

        timer.Start();
    }

    /// <summary>
    /// Opens the settings window, which is now the home for everything that used to be a
    /// command-line flag.
    /// </summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app) return;

        // ShowInFront, not Show: the bar never holds the foreground, so a window it opens lands
        // behind whatever the user was looking at. See WindowOrder.
        new Settings.SettingsWindow(app.Settings, ApplyLiveSettings, app.Restart).ShowInFront();
    }

    /// <summary>
    /// Opens the drift speed simulator. Motion is imperceptible at 1× by design (P6), so this is
    /// the only way to confirm with your eyes that it is running and that it stays inside its slot.
    /// </summary>
    private void OnMotionSpeedClick(object sender, RoutedEventArgs e)
    {
        if (_motion is null)
        {
            MessageBox.Show("Motion is switched off (--no-motion), so there is nothing to speed up.",
                            "Umbra", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new MotionSpeedWindow(_motion, CurrentMetrics, suspended =>
        {
            if (_wear is not null) _wear.Suspended = suspended;
        }).ShowInFront();
    }

    private void OnHeatmapClick(object sender, RoutedEventArgs e)
    {
        if (_wear is null) return;

        // Flush first, so what the window shows is what is on disk.
        _wear.Persist();
        new HeatmapWindow(_wear.Accumulator) { Owner = null }.ShowInFront();
    }

    /// <summary>
    /// Applies the settings that can change without rebuilding the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resting icon size, the motion layer and the row alignment are all properties of what is
    /// drawn, so they take effect immediately. The edge and the monitor are not here: they are
    /// baked into the AppBar registration and still need a restart, which
    /// <see cref="Settings.SettingsWindow"/> says plainly rather than appearing to apply them.
    /// </para>
    /// <para>
    /// <b>Alignment is live because it does not move the reserved rectangle.</b> Both alignments
    /// reserve the same strip along the whole edge — only where the icons sit inside it changes —
    /// so nothing here re-registers the AppBar and §6.3's prohibition is not engaged. It was on the
    /// restart list for no better reason than being next to the edge in the settings window.
    /// </para>
    /// </remarks>
    public void ApplyLiveSettings(Settings.UmbraSettings settings)
    {
        _dormantIconExtent = Math.Clamp(
            settings.DormantIconSize * Metrics.Scale, 1.0, ActiveIconExtent * Metrics.Scale);

        OnPropertyChanged(nameof(IconExtent));
        OnPropertyChanged(nameof(HoverChipExtent));

        ApplyHoverSettings(settings);
        ApplyIconSourceSetting(settings);

        ApplyPlacement(settings.Edge, settings.Alignment, ResolveScreen(settings.Monitor));

        _motion?.SetEnabled(settings.Motion);
        _motion?.Reemit();

        BringUpLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"settings applied live: dormant icon {_dormantIconExtent:F0} px, " +
            $"motion {settings.Motion}, edge {Metrics.Edge}, alignment {Metrics.Alignment}, " +
            $"hover {settings.AppIconHover} {settings.HoverAccent}, " +
            $"applicationIcons {settings.ApplicationIcons}"));
    }

    /// <summary>
    /// Applies whether slots draw their application's icon or their current window's.
    /// </summary>
    /// <remarks>
    /// Both answers are already cached by the resolver, so this picks between them and repaints.
    /// No shell call happens here, which is why it is live rather than on the restart list.
    /// </remarks>
    public void ApplyIconSourceSetting(Settings.UmbraSettings settings)
    {
        if (_tasks.Icons.PreferApplicationIcon == settings.ApplicationIcons) return;

        _tasks.Icons.PreferApplicationIcon = settings.ApplicationIcons;
        _tasks.RefreshIcons();
    }

    /// <summary>
    /// Applies the hover colours and the row's hover treatment.
    /// </summary>
    /// <remarks>
    /// Live, like everything else that only changes what is drawn. The templates bind to these,
    /// and both treatments are built for every slot with one of them collapsed — switching is a
    /// visibility change rather than a retemplate, so the row does not rebuild its containers.
    /// Rebuilding them would be visible for a different reason: a container that is destroyed and
    /// recreated has nowhere to slide from, which is what makes reordering animate at all.
    /// </remarks>
    public void ApplyHoverSettings(Settings.UmbraSettings settings)
    {
        AccentColor = CappedColour(settings.HoverAccent);
        AppIconHoverBrush = CappedBrush(settings.AppIconHoverColour);
        AppIconsUseHolographicHover = settings.AppIconHover == Settings.AppIconHover.Holographic;

        OnPropertyChanged(nameof(AccentColor));
        OnPropertyChanged(nameof(AppIconHoverBrush));
        OnPropertyChanged(nameof(AppIconsUseHolographicHover));
        OnPropertyChanged(nameof(AppIconChipVisibility));
        OnPropertyChanged(nameof(AppIconPaneVisibility));
    }

    /// <summary>
    /// Moves the bar to a different edge, monitor or row alignment, without a restart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>§6.3 is not violated by this.</b> What it forbids is re-registering the reserved
    /// rectangle <i>in steady state</i>, because doing so reflows every maximised window on the
    /// desktop. A deliberate change of edge is not steady state, and reflowing is precisely what
    /// the user has asked for. The prohibition is against doing it behind their back.
    /// </para>
    /// <para>
    /// Alignment alone never touches the registration — both alignments reserve the same strip and
    /// only move the icons inside it — so it takes the cheap path and no window on the desktop is
    /// disturbed.
    /// </para>
    /// </remarks>
    private void ApplyPlacement(CoreBarEdge edge, Alignment alignment, AppBarScreen? screen)
    {
        AppBarScreen target = screen ?? AppBarScreen.FromPrimaryScreen();

        bool movingBar = edge != Metrics.Edge || !SameScreen(target, Screen);
        bool movingRow = alignment != Metrics.Alignment;

        if (!movingBar && !movingRow) return;

        BarMetrics updated = new(
            edge,
            alignment,
            Metrics.Scale,
            Metrics.MeasuredWindows11RowInset,
            Metrics.Slot.IconExtent / Metrics.Scale);

        if (movingBar) Reregister(updated, target);
        else Metrics = updated;

        RaisePlacementProperties();

        // The drift amplitude is derived from the metrics, and the driver reads them through a
        // lambda — but only on its own 2 Hz tick. Republish now, or the content sits for up to half
        // a second carrying an offset computed for the edge it used to be on, which on a change of
        // axis means an offset on the wrong axis entirely.
        _motion?.Reemit();

        // The accumulator is sized to the reserved rectangle in physical pixels, so a new edge or a
        // new monitor is a new grid. Done last, after layout has settled at the new size.
        if (movingBar) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, StartWear);
    }

    /// <summary>Re-registers the space-reserving AppBar for a new edge or monitor.</summary>
    /// <remarks>
    /// Unregister first. Registering a second rectangle before releasing the first leaves Explorer
    /// holding reserved space for an edge nothing occupies any more, and it survives until Explorer
    /// restarts — the same leak <c>ABM_REMOVE</c> on shutdown exists to prevent.
    /// </remarks>
    private void Reregister(BarMetrics updated, AppBarScreen target)
    {
        UnregisterAppBar();

        Metrics = updated;
        Screen = target;
        AppBarEdge = ToAppBarEdge(updated.Edge);

        // Both, because which one is the depth depends on the axis and AppBarWindow reads whichever
        // matches the edge it is registering for.
        DesiredHeight = updated.ReservedExtent;
        DesiredWidth = updated.ReservedExtent;

        RegisterAppBar();
        UpdatePosition();

        BringUpLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"re-registered AppBar: edge {updated.Edge} on {Screen.DeviceName} " +
            $"extent {updated.ReservedExtent} -> {Left},{Top} {Width}x{Height}"));
    }

    /// <summary>
    /// Tells every bound layout property that the geometry moved.
    /// </summary>
    /// <remarks>
    /// All of it, on any placement change. The subset that depends on the edge rather than the
    /// alignment is not worth tracking separately: these are cheap property reads, they happen
    /// only when the user changes a setting, and a list that has to be kept in sync with which
    /// property reads what is a list that will fall out of sync and leave one control behind.
    /// </remarks>
    private void RaisePlacementProperties()
    {
        foreach (string name in (string[])[
            nameof(IsVertical), nameof(ContentBand),
            nameof(RowOrientation), nameof(RowWidth), nameof(RowHeight),
            nameof(RowHorizontalAlignment), nameof(RowVerticalAlignment), nameof(RowMargin),
            nameof(SlotWidth), nameof(SlotHeight),
            nameof(StartHorizontalAlignment), nameof(StartVerticalAlignment),
            nameof(StartHitWidth), nameof(StartHitHeight), nameof(StartGlyphMargin),
            nameof(TrayHorizontalAlignment), nameof(TrayVerticalAlignment), nameof(TrayMargin),
            nameof(TraySlotWidth), nameof(TraySlotHeight), nameof(TrayIconExtent),
            nameof(PillWidth), nameof(PillHeight), nameof(PillMargin),
            nameof(PillHorizontalAlignment), nameof(PillVerticalAlignment),
            nameof(IconExtent), nameof(HoverChipExtent),
            nameof(ClockDate), nameof(ClockMargin), nameof(TooltipPlacement),
        ])
        {
            OnPropertyChanged(name);
        }

        // Not in the list above, because it is computed rather than read: the reserved width is
        // measured from the font, and on a vertical bar there is nothing to reserve.
        if (IsLoaded) MeasureClock();
    }

    /// <summary>
    /// Starts wear sampling at the bar's current physical size, replacing any previous sampler.
    /// </summary>
    /// <remarks>
    /// The outgoing dataset is flushed before it is dropped. It will not be loaded back if the
    /// geometry changed — <c>WearStore.LoadCompatible</c> refuses a grid of a different shape, which
    /// is correct, since per-pixel wear from a horizontal bar means nothing on a vertical one — but
    /// persisting first means the record on disk is whole up to the moment the user moved the bar,
    /// rather than missing its last five minutes.
    /// </remarks>
    private void StartWear()
    {
        double scale = DpiScale <= 0 ? 1.0 : DpiScale;
        int width = Math.Max(1, (int)Math.Round(ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Round(ActualHeight * scale));

        if (_wear is not null)
        {
            if (_wear.Accumulator.Width == width && _wear.Accumulator.Height == height) return;

            _wear.Persist();
            _wear.Dispose();
        }

        _wear = new WearSampler(
            width,
            height,
            () => SceneReader.Read(ContentRoot, this),
            () => DpiScale <= 0 ? 1.0 : DpiScale);

        _wear.Start();

        BringUpLog.Write($"wear accumulator: {width}x{height} physical px ({_wear.Accumulator.SizeInBytes / 1024} KB)");
    }

    /// <summary>
    /// Finds a monitor by device name, falling back to the primary.
    /// </summary>
    /// <remarks>
    /// By name, never by index: indices are reassigned when a monitor is unplugged, so an index
    /// would silently move the bar to a different screen.
    /// </remarks>
    private static AppBarScreen? ResolveScreen(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;

        try
        {
            foreach (AppBarScreen screen in AppBarScreen.FromAllScreens())
                if (string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    return screen;
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            BringUpLog.Write(e, "could not enumerate monitors; staying where we are");
        }

        return null;
    }

    private static bool SameScreen(AppBarScreen a, AppBarScreen? b) =>
        b is not null && string.Equals(a.DeviceName, b.DeviceName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Geometry for this bar, straight from the spec model.</summary>
    /// <remarks>
    /// Replaced wholesale rather than mutated — <c>BarMetrics</c> is immutable, which is what makes
    /// it safe to hand to the motion driver and the wear sampler. Only the row alignment changes at
    /// runtime; the edge and the monitor are still baked into the AppBar registration.
    /// </remarks>
    public BarMetrics Metrics { get; private set; }

    /// <summary>The live window list, as ManagedShell reports it.</summary>
    public ICollectionView Windows { get; }

    /// <summary>Pinned applications (§9 / build order step 9).</summary>
    public PinSettings Pins { get; } = new();

    /// <summary>What the bar actually shows: one slot per application.</summary>
    public ObservableCollection<TaskbarSlot> Slots => _tasks.Slots;

    /// <summary>
    /// The notification icons shown on the bar itself — the system indicators (§11.1).
    /// </summary>
    /// <remarks>Null when tray hosting is off.</remarks>
    public ICollectionView? TrayIcons { get; }

    /// <summary>The notification icons behind the overflow chevron.</summary>
    public ICollectionView? OverflowTrayIcons { get; }

    /// <summary>
    /// Whether the chevron is worth drawing.
    /// </summary>
    /// <remarks>
    /// Hidden rather than disabled when the overflow is empty. A permanently lit affordance that
    /// opens an empty panel is both a lie and, on this bar, a fixed lit pixel for nothing.
    /// </remarks>
    public Visibility TrayOverflowVisibility =>
        OverflowTrayIcons is not null && OverflowTrayIcons.Cast<object>().Any()
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// The notification area's drift offset — the same phase as the icon row, scaled down to what
    /// a tray slot can contain (§6.2, P5).
    /// </summary>
    public TranslateTransform TrayOffset { get; } = new();

    /// <summary>
    /// Drawn extent of notification-area content — tray icons, the network glyph, the chevron.
    /// </summary>
    /// <remarks>
    /// <b>A deliberate deviation from §7.1's 16 px</b>, in the same spirit as the icon row's
    /// dormant size: 16 px was measured against Windows 10's notification area and reads as small
    /// beside a 24 px task icon on this panel. Tried at 20 px on hardware and judged too large by
    /// the project owner; 18 is where it settled.
    /// </remarks>
    internal const double TrayGlyphExtent = 18.0;

    /// <summary>
    /// Pitch of a tray slot, enlarged with the glyph so the gutter is unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Moving the pitch by exactly what the glyph gained keeps the gutter at §7.1's 4 px, and the
    /// gutter is what §6.2 derives drift amplitude from — so the notification area still travels
    /// as far as it did, and the wear argument for moving it at all is unchanged. Growing the
    /// glyph inside the old pitch would have left 2 px of gutter and halved the travel.
    /// </para>
    /// <para>
    /// <b>It does not, on its own, protect P5</b> — that was the first reason written here and it
    /// was wrong. Amplitude is derived from the gutter, so a cramped slot asks for less travel and
    /// still contains it. What breaks containment is reading an amplitude derived from a slot we
    /// are not drawing: <see cref="BarMetrics.TrayDriftRatio"/> is computed from 16-in-24, and
    /// applying it to a 20 px glyph in a 24 px pitch puts about 2.4 px of travel into 2 px of
    /// gutter. That is why <see cref="TrayDriftRatio"/> here is derived from
    /// <see cref="TraySlotMetrics"/>, and why the test pins the ratio rather than the sizes.
    /// </para>
    /// </remarks>
    internal const double TrayPitchExtent = BarMetrics.BaseTrayPitch + (TrayGlyphExtent - BarMetrics.BaseTrayIconExtent);

    /// <summary>The notification area's slot, at the size this bar actually draws it.</summary>
    private SlotMetrics TraySlotMetrics =>
        new(TrayPitchExtent * Metrics.Scale, TrayGlyphExtent * Metrics.Scale);

    /// <summary>
    /// How much less the notification area drifts than the icon row.
    /// </summary>
    /// <remarks>
    /// Derived from the slot we draw rather than from <see cref="BarMetrics.TrayDriftRatio"/>,
    /// which is derived from the spec's. They agree today, because the pitch grew with the glyph;
    /// reading the spec's would stop being correct the moment someone changed one without the
    /// other, which is exactly the mistake worth making impossible.
    /// </remarks>
    public double TrayDriftRatio => TrayDriftRatioFor(Metrics);

    /// <summary>
    /// The tray's share of the row's drift, for the slot this bar actually draws.
    /// </summary>
    /// <remarks>
    /// Static so it can be asserted without a window — a <c>BarWindow</c> needs a shell host, and
    /// this is the number that decides whether the notification area stays inside its slots.
    /// </remarks>
    internal static double TrayDriftRatioFor(BarMetrics metrics)
    {
        SlotMetrics drawn = new(TrayPitchExtent * metrics.Scale, TrayGlyphExtent * metrics.Scale);

        return metrics.Slot.MaxAmplitude <= 0.0
            ? 0.0
            : drawn.MaxAmplitude / metrics.Slot.MaxAmplitude;
    }

    /// <summary>Tray slot size along the bar's long axis.</summary>
    public double TraySlotWidth => IsVertical ? Metrics.ContentBand : TraySlotMetrics.Pitch;

    /// <inheritdoc cref="TraySlotWidth"/>
    public double TraySlotHeight => IsVertical ? TraySlotMetrics.Pitch : Metrics.ContentBand;

    /// <summary>Drawn extent of a tray icon.</summary>
    public double TrayIconExtent => TraySlotMetrics.IconExtent;

    /// <summary>
    /// Width of the overflow chevron's glyph.
    /// </summary>
    /// <remarks>
    /// Narrower than the square glyphs beside it, because a chevron is a wide shallow shape and
    /// matching its <i>width</i> to theirs would make it the largest thing in the group.
    /// </remarks>
    public double TrayChevronWidth => TrayGlyphExtent * 0.7;

    /// <inheritdoc cref="TrayChevronWidth"/>
    public double TrayChevronHeight => TrayChevronWidth * 4.0 / 9.0;

    /// <summary>The notification area sits at the trailing end, opposite the icon row (§7.1).</summary>
    public HorizontalAlignment TrayHorizontalAlignment =>
        IsVertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;

    /// <inheritdoc cref="TrayHorizontalAlignment"/>
    public VerticalAlignment TrayVerticalAlignment =>
        IsVertical ? VerticalAlignment.Top : VerticalAlignment.Center;

    /// <summary>
    /// The clock's own drift offset. It shares the content group's drift but jitters on different
    /// frequencies (§6.4).
    /// </summary>
    /// <remarks>
    /// v1 gave the clock a phase offset on the icon row's frequency. Wear integrates over many
    /// cycles, so a phase offset produces an identical accumulated distribution — it decorrelates
    /// the instantaneous appearance and nothing else. Different frequencies are the fix.
    /// </remarks>
    public TranslateTransform ClockOffset { get; } = new();

    /// <summary>
    /// Width held for the clock whatever it is currently showing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The clock changes width when the pointer arrives</b> — seconds appear at Tier 1 (§6.4)
    /// and the date line appears under them — and it is the last thing in a right-aligned row, so
    /// everything to its left was shoved sideways every time the bar was hovered. Reserving the
    /// wide width makes the row still.
    /// </para>
    /// <para>
    /// Measured rather than guessed, with the clock's own typeface at its own size, because the
    /// width of <c>00:00:00</c> is a property of the font and the user's digits, not a number to
    /// write down. Measured once: it changes only with the edge or the DPI.
    /// </para>
    /// <para>
    /// The <i>time</i> line is what is measured. The date line appears at the same moment and is
    /// narrower at every locale checked here, but a locale where it is not would shift the row
    /// again — visibly, and only for that user.
    /// </para>
    /// </remarks>
    public double ClockMinWidth { get; private set; } = double.NaN;

    /// <summary>
    /// Clearance between the clock and whatever precedes it.
    /// </summary>
    /// <remarks>
    /// Wider than the gap between tray icons, because the clock is text and its neighbours are
    /// glyphs: without it the language indicator reads as part of the time.
    /// </remarks>
    internal const double ClockLeadingGap = 12.0;

    /// <inheritdoc cref="ClockLeadingGap"/>
    public Thickness ClockMargin => IsVertical
        ? new Thickness(0, ClockLeadingGap, 0, 0)
        : new Thickness(ClockLeadingGap, 0, 0, 0);

    /// <summary>Current time, refreshed once a minute.</summary>
    public string ClockTime { get; private set; } = string.Empty;

    /// <summary>Current date, shown from Tier 1 upward.</summary>
    public string ClockDate { get; private set; } = string.Empty;

    /// <summary>
    /// Seconds appear from Tier 1 upward — that is, only while the pointer is near the bar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §6.4 removes seconds outright, and its reasoning is about repaints rather than wear: a
    /// ticking seconds field is "high-frequency change in a tiny fixed area with almost no
    /// informational value". Note it is not especially costly in <i>wear</i> terms — the digits
    /// vary, so the light is spread across the segment shapes rather than burned into one pattern.
    /// What it costs is a repaint every second forever, which works directly against §6.6's
    /// requirement that the display be allowed to idle so the panel can run its compensation
    /// cycles.
    /// </para>
    /// <para>
    /// Tying them to the proximity band keeps both: seconds when someone is actually looking, and
    /// a bar that repaints once a minute the other ~92% of the time.
    /// </para>
    /// </remarks>
    public bool ShowsSeconds => TierLevel >= 1.0;

    /// <summary>The date line appears from Tier 1 upward (§7.1, §7.3).</summary>
    public bool ShowsDate => TierLevel >= 1.0;

    /// <summary>
    /// The show-desktop control's hairline, which appears from Tier 1 like the date.
    /// </summary>
    /// <remarks>
    /// <b>Its hit area is always there; only the line comes and goes.</b> Windows draws its sliver
    /// permanently, which on this panel would be a lit column of pixels in a fixed position for
    /// the whole life of the display — the exact shape §2.3's duty budget exists to refuse. The
    /// control still works when it is invisible, and it is in the screen corner, which is the one
    /// place a pointer can always land without aiming.
    /// </remarks>
    public bool ShowsShowDesktopLine => TierLevel >= 1.0;

    /// <summary>
    /// Hit extent of the show-desktop control along the bar's long axis.
    /// </summary>
    /// <remarks>
    /// Exactly the trailing margin the row already holds back — §6.3's drift inset plus
    /// <see cref="TrailingEdgePadding"/> — so the control claims space that was empty rather than
    /// taking any from the clock. Like the Start button (§6.5) its hit area runs into the screen
    /// corner while its drawn content stays inboard of the drift inset, where it cannot clip.
    /// </remarks>
    public double ShowDesktopHitExtent => Metrics.RowInset + TrailingEdgePadding + ShowDesktopWidth;

    /// <summary>
    /// How much of the control's width is its own, beyond the margin the row already held back.
    /// </summary>
    /// <remarks>
    /// Windows' own sliver is about this wide. The first cut claimed only the existing trailing
    /// margin, which made it about 19 px on this machine and easy to miss entirely.
    /// </remarks>
    internal const double ShowDesktopWidth = 16.0;

    /// <inheritdoc cref="ShowDesktopHitExtent"/>
    public double ShowDesktopHitWidth => IsVertical ? Metrics.ContentBand : ShowDesktopHitExtent;

    /// <inheritdoc cref="ShowDesktopHitExtent"/>
    public double ShowDesktopHitHeight => IsVertical ? ShowDesktopHitExtent : Metrics.ContentBand;

    /// <summary>Which corner the show-desktop control occupies.</summary>
    public HorizontalAlignment ShowDesktopHorizontalAlignment =>
        IsVertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;

    /// <inheritdoc cref="ShowDesktopHorizontalAlignment"/>
    public VerticalAlignment ShowDesktopVerticalAlignment =>
        IsVertical ? VerticalAlignment.Bottom : VerticalAlignment.Center;

    /// <summary>The hairline runs across the bar's short axis, as Windows' sliver does.</summary>
    public double ShowDesktopLineWidth => IsVertical ? 16 : 1;

    /// <inheritdoc cref="ShowDesktopLineWidth"/>
    public double ShowDesktopLineHeight => IsVertical ? 1 : 16;

    /// <summary>
    /// The input language indicator's own drift offset.
    /// </summary>
    /// <remarks>
    /// Its own <see cref="TranslateTransform"/>, carrying the clock's numbers. Sharing the clock's
    /// instance across two elements is the failure that stopped the running indicator rendering
    /// at all: a <c>Transform</c> assigned to several elements is not a shared value, it is one
    /// object with one parent, and WPF drops the rest.
    /// </remarks>
    public TranslateTransform LanguageOffset { get; } = new();

    /// <summary>The three-letter input language name, as the Windows taskbar shows it.</summary>
    public string LanguageName => _language.Name;

    /// <summary>The network indicator's drift offset — its own transform, as every element has.</summary>
    public TranslateTransform NetworkOffset { get; } = new();

    /// <summary>Wi-Fi, Ethernet or a slashed globe (§11.1).</summary>
    public Geometry NetworkGlyph => NetworkGlyphs.For(_network.Kind);

    /// <summary>What the indicator says on hover.</summary>
    public string NetworkDescription => _network.Description;

    /// <summary>
    /// Opens Quick Settings — the system cluster's only action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sound and network are one control, as they are on Windows 11.</b> The real taskbar has a
    /// single button covering volume, network and battery that opens Quick Settings; treating them
    /// as separate targets meant two hover chips side by side and a hairline between them where
    /// neither lit. The pinned strip is exactly the right membership for it — it holds the four
    /// system GUIDs and nothing else, because everything without one goes to the overflow.
    /// </para>
    /// <para>
    /// Left click only. A right click still reaches the icon underneath, so Windows' own volume
    /// menu is unaffected; the cluster never sees it.
    /// </para>
    /// <para>
    /// Our panels close first and Windows' are left alone, because <c>ShowActionCenter</c> is a
    /// toggle and dismissing an open Quick Settings here would fight the call that follows.
    /// </para>
    /// </remarks>
    private void OnSystemClusterClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        PlayPress(SystemClusterScale);
        ClearPanelsFor(BarPanel.None);
        ManagedShell.Common.Helpers.ShellHelper.ShowActionCenter();
    }

    /// <summary>
    /// Opens the notification centre, as clicking the Windows clock does.
    /// </summary>
    /// <remarks>
    /// A toggle, like every other shell surface, so our own panels are cleared and Windows' are
    /// not — see <see cref="DismissWindowsSurfaces"/> for why dismissing before a toggle would
    /// undo itself.
    /// </remarks>
    private void OnClockClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        PlayPress(ClockScale);
        ClearPanelsFor(BarPanel.None);
        ManagedShell.Common.Helpers.ShellHelper.ShowNotificationCenter();
    }

    /// <summary>
    /// Minimises everything and shows the desktop, as the sliver at the end of the Windows taskbar
    /// does — and restores it all on a second click.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Through the shell's own COM object, not by messaging the taskbar.</b> The usual trick is
    /// <c>WM_COMMAND 419</c> to <c>Shell_TrayWnd</c>, which cannot work here: Explorer's taskbar is
    /// suppressed and ManagedShell registers a <c>Shell_TrayWnd</c> of our own, so
    /// <c>FindWindow</c> would hand back our window and the message would go nowhere. It would
    /// fail silently, which is the worst available outcome.
    /// </para>
    /// <para>
    /// Late-bound because <c>Shell32</c>'s interop assembly is a dependency worth not taking for
    /// one call. <c>ToggleDesktop</c> rather than <c>MinimizeAll</c>, because the Windows control
    /// restores what it minimised when clicked again and users rely on that.
    /// </para>
    /// </remarks>
    private void OnShowDesktopClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        PlayPress(ShowDesktopScale);
        ClearPanelsFor(BarPanel.None);
        DismissWindowsSurfaces();

        try
        {
            Type? shell = Type.GetTypeFromProgID("Shell.Application");
            if (shell is null)
            {
                BringUpLog.Write("show desktop: Shell.Application is not registered");
                return;
            }

            object? instance = Activator.CreateInstance(shell);
            shell.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod,
                               binder: null, target: instance, args: null);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or MissingMethodException
                                       or System.Reflection.TargetInvocationException)
        {
            BringUpLog.Write(ex, "show desktop failed");
        }
    }

    /// <summary>
    /// Whether to draw it at all.
    /// </summary>
    /// <remarks>
    /// Hidden with one installed language, as Windows hides its own: the indicator would be a
    /// permanently lit patch of text that cannot ever change, which is the worst shape a pixel can
    /// have on this panel and conveys nothing in exchange.
    /// </remarks>
    public Visibility LanguageVisibility =>
        _language.HasChoice && _language.Name.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Cycles the foreground window's input language, as clicking Windows' own does.</summary>
    private void OnLanguageClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        PlayPress(LanguageScale);
        ClearPanelsFor(BarPanel.None);
        _language.CycleForeground(GetForegroundWindow());
    }

    /// <summary>
    /// Which side of a control its tooltip opens on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off the bar, whichever edge the bar is on.</b> WPF's default is
    /// <see cref="PlacementMode.Mouse"/>, which puts the tooltip under the pointer — and the
    /// pointer is on the icon, so the label covered the thing it was labelling. Windows puts its
    /// own above the taskbar for the same reason.
    /// </para>
    /// <para>
    /// It has to follow the edge rather than being a constant: <c>Top</c> on a top-edge bar would
    /// open the tooltip off the screen, and on a vertical bar it would lie along the bar covering
    /// the icons above.
    /// </para>
    /// <para>
    /// Set per control rather than once on the window. The <c>ToolTipService</c> delays inherit
    /// down the visual tree and this one does not, which is the sort of asymmetry that is only
    /// discoverable by trying it.
    /// </para>
    /// </remarks>
    public PlacementMode TooltipPlacement => Metrics.Edge switch
    {
        CoreBarEdge.Top => PlacementMode.Bottom,
        CoreBarEdge.Left => PlacementMode.Right,
        CoreBarEdge.Right => PlacementMode.Left,
        _ => PlacementMode.Top,
    };

    /// <summary>The Start button sits at the aligned end, ahead of the icon row.</summary>
    public HorizontalAlignment StartHorizontalAlignment =>
        IsVertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;

    /// <inheritdoc cref="StartHorizontalAlignment"/>
    public VerticalAlignment StartVerticalAlignment =>
        IsVertical ? VerticalAlignment.Bottom : VerticalAlignment.Center;

    /// <summary>
    /// Hit width, reaching the screen corner on a horizontal bar (§6.5).
    /// </summary>
    public double StartHitWidth => IsVertical ? Metrics.ReservedExtent : Metrics.RowInset + Metrics.Slot.Pitch;

    /// <inheritdoc cref="StartHitWidth"/>
    public double StartHitHeight => IsVertical ? Metrics.RowInset + Metrics.Slot.Pitch : Metrics.ReservedExtent;

    /// <summary>
    /// Pushes the glyph off the corner to the Windows 11 inset, leaving the hit rectangle behind it
    /// reaching all the way in.
    /// </summary>
    public Thickness StartGlyphMargin => IsVertical
        ? new Thickness(0, 0, 0, Metrics.RowInset)
        : new Thickness(Metrics.RowInset, 0, 0, 0);

    /// <summary>Holds the tray off the trailing corner, mirroring the row inset (§6.3).</summary>
    /// <summary>
    /// Extra space between the clock and the screen edge, beyond the drift headroom.
    /// </summary>
    /// <remarks>
    /// §6.3 insets the trailing end by the peak drift offset so the clock cannot clip at the
    /// corner, which is a correctness floor rather than a layout choice — at that inset alone the
    /// clock sits hard against the edge. This is the breathing room on top.
    /// </remarks>
    internal const double TrailingEdgePadding = 8.0;

    public Thickness TrayMargin => IsVertical
        ? new Thickness(0, TrailingReserved, 0, 0)
        : new Thickness(0, 0, TrailingReserved, 0);

    /// <summary>
    /// Everything held back at the trailing end: the drift inset, the breathing room, and the
    /// show-desktop control.
    /// </summary>
    /// <remarks>
    /// The show-desktop control is a sibling of the trailing group rather than a member of it, so
    /// the group has to be moved inboard by its width or the two would overlap — the clock would
    /// be drawn under a transparent hit area, and clicking the time would show the desktop.
    /// </remarks>
    private double TrailingReserved => Metrics.RowInset + TrailingEdgePadding + ShowDesktopWidth;

    /// <summary>Slot pitch — the hit target's extent along the bar's long axis. 44 DIP.</summary>
    public double SlotPitch => Metrics.Slot.Pitch;

    /// <summary>
    /// Drawn icon extent at the current tier: the dormant size while the bar is monochrome, full
    /// size once it is in colour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shrink exists to widen the gutter, because §6.2 derives drift amplitude from the gutter
    /// — a smaller icon travels further and so each screen pixel beneath it rests more. Paying for
    /// that with a permanently smaller icon is a poor trade, and it is not necessary: the wear that
    /// matters accrues at Tier 0 and Tier 1, which together run ~100% duty (P3), while Tier 2 is
    /// ~0.1%. Shrinking only while dormant keeps essentially the whole benefit and gives back the
    /// whole visual cost.
    /// </para>
    /// <para>
    /// The size rides <see cref="TierRender.ChromaBlend"/>, the same Tier 1 → 2 ramp the colour
    /// uses, so the icon reaches full size exactly as it reaches full chroma. Below Tier 1 the
    /// blend is zero and the icon sits at its dormant size.
    /// </para>
    /// <para>
    /// <b>The hit slot never changes.</b> Only the drawn extent moves, so clicking is unaffected at
    /// any size and P5 holds throughout — see <see cref="CurrentMetrics"/> for the amplitude side
    /// of that.
    /// </para>
    /// </remarks>
    public double IconExtent
    {
        get
        {
            double dormant = _dormantIconExtent > 0 ? _dormantIconExtent : Metrics.Slot.IconExtent;
            double full = ActiveIconExtent * Metrics.Scale;

            return IconSizeRamp.Extent(dormant, full, SizeBlend);
        }
    }

    /// <summary>
    /// Margin between the drawn icon and the hover chip behind it, per side.
    /// </summary>
    /// <remarks>
    /// <b>2 px is not a taste decision, it is the largest value that keeps P5.</b> The chip is
    /// concentric with the icon and drifts with it, so its outer edge sits at
    /// <c>e/2 + margin + peakOffset</c> from the slot centre. §6.2 already holds back 5% of the
    /// pitch — 2.2 px at a 44 px pitch — as a safety margin inside the gutter, and that is exactly
    /// the room this borrows. Any larger and the chip leaves the slot at the drift extremes, which
    /// P5 forbids and which would be visible only occasionally. <c>HoverChipStaysInItsSlot</c>
    /// pins it across every icon size and tier level.
    /// </remarks>
    internal const double HoverChipMargin = 2.0;

    /// <summary>
    /// Icon extent once the bar is engaged — slightly above §7.1's 24 px.
    /// </summary>
    /// <remarks>
    /// A deliberate deviation, asked for so the icon reads as lifting toward the pointer rather
    /// than merely returning to normal. It stays inside P5 with room to spare: at 26 px the gutter
    /// is 9 px, §6.2's amplitude falls to ±6.46 px, and the icon plus its hover chip reaches
    /// 21.46 px against a 22 px half-slot. Anything much beyond this starts eating the chip's
    /// margin, so <c>HoverChipTests</c> checks the pair rather than the icon alone.
    /// </remarks>
    internal const double ActiveIconExtent = 26.0;

    /// <summary>
    /// Extent of the hover chip drawn behind an icon the pointer is over.
    /// </summary>
    /// <remarks>
    /// Tracks <see cref="IconExtent"/>, so it shrinks and grows with the icon rather than being a
    /// fixed cell. A fixed chip would reveal the drift — the icon would sit visibly off-centre
    /// inside it at the extremes — which is precisely what P6 asks the motion layer not to do.
    /// </remarks>
    public double HoverChipExtent => IconExtent + 2 * HoverChipMargin;

    /// <summary>How far along the dormant → full-size ramp the icon currently is, 0 to 1.</summary>
    private double SizeBlend =>
        TierPalette.Resolve(BarElement.RunningIcon, TierLevel).ChromaBlend;

    /// <summary>
    /// Metrics for the extent actually being drawn right now, which is what the motion layer must
    /// derive its amplitude from.
    /// </summary>
    /// <remarks>
    /// A grown icon has a narrower gutter and therefore a smaller permitted amplitude. Deriving
    /// from the dormant extent and then drawing a bigger icon would push it out of its slot at the
    /// extremes of the drift, which P5 forbids outright. Recomputing is cheap —
    /// <see cref="BarMetrics"/> is a readonly record struct and every figure on it is derived.
    /// </remarks>
    public BarMetrics CurrentMetrics => new(
        Metrics.Edge,
        Metrics.Alignment,
        Metrics.Scale,
        Metrics.MeasuredWindows11RowInset,
        IconExtent / Metrics.Scale);

    /// <summary>Content band across the bar's short axis. 56 DIP vertical, 48 horizontal.</summary>
    public double ContentBand => Metrics.ContentBand;

    /// <summary>True when the bar runs down a screen side rather than across the top or bottom.</summary>
    public bool IsVertical => Metrics.Edge.IsVertical();

    /// <summary>Icons stack along the bar's long axis.</summary>
    public Orientation RowOrientation => IsVertical ? Orientation.Vertical : Orientation.Horizontal;

    /// <summary>
    /// The row sits at the aligned end: bottom on a vertical bar (§7.3), left on a horizontal one
    /// (§7.1), so the Start button ends up adjacent to where the Start menu actually opens.
    /// </summary>
    public VerticalAlignment RowVerticalAlignment =>
        IsVertical && Metrics.Alignment == Alignment.Start
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Center;

    /// <inheritdoc cref="RowVerticalAlignment"/>
    public HorizontalAlignment RowHorizontalAlignment =>
        !IsVertical && Metrics.Alignment == Alignment.Start
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Center;

    /// <summary>Short axis is fixed to the content band; the long axis sizes to its content.</summary>
    public double RowWidth => IsVertical ? ContentBand : double.NaN;

    /// <inheritdoc cref="RowWidth"/>
    public double RowHeight => IsVertical ? double.NaN : ContentBand;

    /// <summary>One slot: pitch along the long axis, content band across the short one.</summary>
    public double SlotWidth => IsVertical ? ContentBand : SlotPitch;

    /// <inheritdoc cref="SlotWidth"/>
    public double SlotHeight => IsVertical ? SlotPitch : ContentBand;

    /// <summary>
    /// Inset from the aligned end (§6.3). Left-alignment leaves nowhere for negative drift to go,
    /// so the row is held off the corner by the larger of Windows 11's own padding and the peak
    /// drift offset.
    /// </summary>
    /// <remarks>
    /// The Start button occupies the first slot at the aligned end (§7.1), so the application row
    /// begins one pitch further along.
    /// </remarks>
    public Thickness RowMargin => Metrics.Alignment == Alignment.Center
        ? default
        : IsVertical
            ? new Thickness(0, 0, 0, Metrics.RowInset + Metrics.Slot.Pitch)
            : new Thickness(Metrics.RowInset + Metrics.Slot.Pitch, 0, 0, 0);

    /// <summary>
    /// The continuous tier level, 0..2, driven by pointer proximity (§3.4). A dependency property
    /// so that every tier-dependent binding re-evaluates as the ramp moves.
    /// </summary>
    public static readonly DependencyProperty TierLevelProperty = DependencyProperty.Register(
        nameof(TierLevel), typeof(double), typeof(BarWindow), new PropertyMetadata(0.0, OnTierLevelChanged));

    private static void OnTierLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // The clock's date line and its seconds both appear from Tier 1, and both are bound through
        // plain properties rather than the dependency property, so they need telling.
        if (d is not BarWindow bar) return;

        bar.OnPropertyChanged(nameof(ShowsDate));
        bar.OnPropertyChanged(nameof(ShowsSeconds));
        bar.OnPropertyChanged(nameof(ShowsShowDesktopLine));

        // The icon grows with the tier, and its permitted drift amplitude shrinks as it does.
        // Republish the offset now rather than waiting up to half a second for the next motion
        // tick, or the icon would briefly be drawn large while still carrying a dormant-sized
        // offset -- outside its own slot, which is exactly what P5 forbids.
        bar.OnPropertyChanged(nameof(IconExtent));
        bar.OnPropertyChanged(nameof(HoverChipExtent));
        bar._motion?.Reemit();

        // Rebuild the string immediately rather than waiting up to a second for the next tick,
        // so seconds appear as the bar lights rather than after it.
        bar.OnClockTick(null, EventArgs.Empty);
    }

    /// <inheritdoc cref="TierLevelProperty"/>
    public double TierLevel
    {
        get => (double)GetValue(TierLevelProperty);
        set => SetValue(TierLevelProperty, value);
    }

    /// <summary>
    /// True while the Start menu is open, or briefly during the press animation. Colours the glyph.
    /// </summary>
    public static readonly DependencyProperty StartActiveProperty = DependencyProperty.Register(
        nameof(StartActive), typeof(bool), typeof(BarWindow), new PropertyMetadata(false));

    /// <inheritdoc cref="StartActiveProperty"/>
    public bool StartActive
    {
        get => (bool)GetValue(StartActiveProperty);
        set => SetValue(StartActiveProperty, value);
    }

    /// <summary>
    /// True when the Start glyph should be blue: the menu is open, or the pointer is on it.
    /// </summary>
    /// <remarks>
    /// One flag for both reasons, because both want the same treatment. The glyph used to switch
    /// colour outright, which reads as a glitch rather than a response — the blue is now a second
    /// set of squares cross-fading over the grey, at §3.4's 120 ms attack and a shorter release
    /// than the tier ramp uses, because a pointer leaving the Start button is unambiguous in a way
    /// that a pointer drifting near the bar is not.
    /// </remarks>
    public static readonly DependencyProperty StartHighlightedProperty = DependencyProperty.Register(
        nameof(StartHighlighted), typeof(bool), typeof(BarWindow), new PropertyMetadata(false));

    /// <inheritdoc cref="StartHighlightedProperty"/>
    public bool StartHighlighted
    {
        get => (bool)GetValue(StartHighlightedProperty);
        set => SetValue(StartHighlightedProperty, value);
    }

    /// <summary>The Windows logo blue, capped to the Tier 2 luminance ceiling (§3.2).</summary>
    public System.Windows.Media.Brush StartAccentBrush { get; } = CreateStartAccent();

    /// <summary>
    /// The hover accent, as a <see cref="System.Windows.Media.Color"/> so gradients can use it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cyan rather than the Start button's blue, because the holographic sweep wants a colour
    /// that reads as light passing over a surface rather than as a second accent competing with
    /// Start. <b>Capped to the same Tier 2 luminance ceiling</b> — a hover effect is the easiest
    /// place in a design to smuggle past the brightest thing §2.3 allows, and this one is drawn
    /// over pixels that were black.
    /// </para>
    /// <para>
    /// Exposed as a colour because a <c>GradientStop</c> takes one; the brush form would have to
    /// be unpacked in XAML, which cannot be done.
    /// </para>
    /// </remarks>
    public System.Windows.Media.Color AccentColor { get; private set; } =
        CappedColour(Settings.UmbraSettings.DefaultHoverAccent);

    /// <summary>The plain chip behind a hovered application icon, when that is the choice.</summary>
    public System.Windows.Media.Brush AppIconHoverBrush { get; private set; } =
        CappedBrush(Settings.UmbraSettings.DefaultAppIconHoverColour);

    /// <summary>Whether the application icons use the holographic pane rather than the chip.</summary>
    public bool AppIconsUseHolographicHover { get; private set; }

    /// <inheritdoc cref="AppIconsUseHolographicHover"/>
    public Visibility AppIconChipVisibility =>
        AppIconsUseHolographicHover ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc cref="AppIconsUseHolographicHover"/>
    public Visibility AppIconPaneVisibility =>
        AppIconsUseHolographicHover ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// A colour from the settings, capped to what Tier 2 is allowed to emit.
    /// </summary>
    /// <remarks>
    /// <b>The cap is the whole reason a colour setting is safe to offer.</b> §2.3's budget is
    /// about light, not hue, and a picker with no ceiling is an invitation to put a full-intensity
    /// white behind every icon on an OLED panel. Capping leaves the user the choice that is
    /// genuinely theirs — what colour it is — and keeps the one that is not.
    /// </remarks>
    private static System.Windows.Media.Color CappedColour(string hex)
    {
        try
        {
            Umbra.Core.Color.Rgb capped = Umbra.Core.Color.Srgb.CapLuminance(
                Umbra.Core.Color.Rgb.FromHex(hex), TierPalette.Tier2LuminanceCap);

            return System.Windows.Media.Color.FromRgb(capped.R, capped.G, capped.B);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            // The settings reader validates the shape, so this is the hand-edited file that got
            // past it. The default is a better answer than a crash on the way to first paint.
            BringUpLog.Write($"settings: '{hex}' is not a colour; using the default");

            return CappedColour(Settings.UmbraSettings.DefaultHoverAccent);
        }
    }

    private static System.Windows.Media.Brush CappedBrush(string hex)
    {
        System.Windows.Media.SolidColorBrush brush = new(CappedColour(hex));
        brush.Freeze();

        return brush;
    }

    private static System.Windows.Media.Brush CreateStartAccent()
    {
        Umbra.Core.Color.Rgb capped = Umbra.Core.Color.Srgb.CapLuminance(
            Umbra.Core.Color.Rgb.FromHex("#4CC2FF"), TierPalette.Tier2LuminanceCap);

        System.Windows.Media.SolidColorBrush brush = new(
            System.Windows.Media.Color.FromRgb(capped.R, capped.G, capped.B));

        brush.Freeze();
        return brush;
    }

    /// <summary>Running-indicator pill geometry — it rotates with the bar (§7.3).</summary>
    public double PillWidth => IsVertical ? 3 : 16;

    /// <inheritdoc cref="PillWidth"/>
    public double PillHeight => IsVertical ? 16 : 3;

    /// <summary>The pill sits on the screen-edge side of the icon.</summary>
    public HorizontalAlignment PillHorizontalAlignment => Metrics.Edge switch
    {
        CoreBarEdge.Left => HorizontalAlignment.Left,
        CoreBarEdge.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center,
    };

    /// <inheritdoc cref="PillHorizontalAlignment"/>
    public VerticalAlignment PillVerticalAlignment => Metrics.Edge switch
    {
        CoreBarEdge.Top => VerticalAlignment.Top,
        CoreBarEdge.Bottom => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Center,
    };

    /// <summary>Holds the pill just clear of the screen edge.</summary>
    public Thickness PillMargin => Metrics.Edge switch
    {
        CoreBarEdge.Left => new Thickness(PillEdgeClearance, 0, 0, 0),
        CoreBarEdge.Right => new Thickness(0, 0, PillEdgeClearance, 0),
        CoreBarEdge.Top => new Thickness(0, PillEdgeClearance, 0, 0),
        _ => new Thickness(0, 0, 0, PillEdgeClearance),
    };

    /// <summary>Closes the bar, which the AppBar base class otherwise refuses to do.</summary>
    public void CloseForShutdown()
    {
        AllowClose = true;
        Close();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => CloseForShutdown();

    /// <summary>
    /// Opens the Windows Start menu.
    /// </summary>
    /// <remarks>
    /// This is a synthesised Win keypress and there is nothing else on offer — no API accepts a
    /// position, which is why §7.0 accepts that the menu cannot be moved. On the bottom edge with
    /// left alignment it lands beneath this button anyway; on the other three edges it opens at the
    /// bottom of the screen regardless, which is documented and accepted rather than worked around.
    ///
    /// It is global input injection, so it fails silently when a higher-integrity window has focus.
    /// </remarks>
    private void OnStartClick(object sender, MouseButtonEventArgs e)
    {
        // Clicking Start toggles: if the menu is already open this press closes it, and the colour
        // must go with it. Granting the press grace here would hold the glyph blue for another
        // 450 ms after the menu had gone, which reads as lag.
        bool opening = !_startMenu.IsOpen;

        _startPressedAt = opening ? DateTime.UtcNow : DateTime.MinValue;
        StartActive = opening;

        PlayStartPress();
        ManagedShell.Common.Helpers.ShellHelper.ShowStartMenu();
        e.Handled = true;
    }

    /// <summary>
    /// A quick press-and-spring on the Start glyph: it dips and twists, then overshoots back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §10 lists "animation for its own sake" as a non-goal, and the reason given is that any
    /// animation keeping otherwise-dark pixels lit is a regression. This one does not: it lasts
    /// 320 ms, it only ever runs in response to a deliberate click, and the pointer is necessarily
    /// on the bar at the time — so the glyph is already at Tier 2, and nothing stays lit afterwards.
    /// </para>
    /// <para>
    /// Skipped entirely when the system's animation setting is off, the same signal §6.6 uses to
    /// stop the drift.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Press feedback for the small controls at the trailing end of the bar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same dip and spring the Start button and the application icons already play, at a
    /// smaller amplitude because these are smaller things: a 22% squash reads as a stumble on a
    /// 20 px glyph.
    /// </para>
    /// <para>
    /// <b>It costs nothing in wear terms</b>, which is the question any animation on this bar has
    /// to answer. It runs for 320 ms while the pointer is necessarily on the bar, so the whole
    /// group is at Tier 2 regardless, and nothing is left lit when it ends.
    /// </para>
    /// <para>
    /// The scale lives on a wrapper rather than on the drawn element, because the drawn element's
    /// <c>RenderTransform</c> is already the drift offset. One transform property, two owners, is
    /// the failure that stopped the running indicator rendering at all.
    /// </para>
    /// </remarks>
    private static void PlayPress(ScaleTransform scale)
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        QuadraticEase down = new() { EasingMode = EasingMode.EaseOut };
        BackEase back = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 };

        DoubleAnimationUsingKeyFrames Squash() => new()
        {
            KeyFrames =
            {
                new EasingDoubleKeyFrame(0.86, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), down),
                new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), back),
            },
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Squash());
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Squash());
    }

    private void PlayStartPress()
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        // Ease out on the way down so it responds instantly, and a slight overshoot on the way back
        // so it springs rather than merely returning.
        QuadraticEase down = new() { EasingMode = EasingMode.EaseOut };
        BackEase back = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 };

        DoubleAnimationUsingKeyFrames Squash() => new()
        {
            KeyFrames =
            {
                new EasingDoubleKeyFrame(0.78, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), down),
                new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), back),
            },
        };

        StartScale.BeginAnimation(ScaleTransform.ScaleXProperty, Squash());
        StartScale.BeginAnimation(ScaleTransform.ScaleYProperty, Squash());

        StartRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new EasingDoubleKeyFrame(-14.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), down),
                new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), back),
            },
        });
    }

    /// <summary>Win+X, the same way.</summary>
    private void OnStartContextMenu(object sender, MouseButtonEventArgs e)
    {
        ManagedShell.Common.Helpers.ShellHelper.ShowStartContextMenu();
        e.Handled = true;
    }

    /// <summary>
    /// Refreshes the clock. Ticks every second but only touches the UI when the displayed minute
    /// changes — the display has no seconds, so a per-second repaint would light the same pixels
    /// sixty times for one change.
    /// </summary>
    /// <summary>
    /// Works out how much width to hold for the clock, from the clock's own font.
    /// </summary>
    /// <remarks>
    /// Nothing is reserved on a vertical bar: the whole content band is the width there, so the
    /// clock cannot push anything sideways in the first place. See <see cref="ClockMinWidth"/>.
    /// </remarks>
    private void MeasureClock()
    {
        double previous = ClockMinWidth;

        if (IsVertical)
        {
            ClockMinWidth = double.NaN;
        }
        else
        {
            FormattedText widest = new(
                "00:00:00",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(ClockTimeText.FontFamily, ClockTimeText.FontStyle,
                             ClockTimeText.FontWeight, ClockTimeText.FontStretch),
                ClockTimeText.FontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            // Rounded up: a fractional shortfall still reflows the row, and a spare pixel costs
            // nothing but layout.
            ClockMinWidth = Math.Ceiling(widest.Width) + 1;
        }

        if (!ClockMinWidth.Equals(previous)) OnPropertyChanged(nameof(ClockMinWidth));
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.Now;
        string time = now.ToString(ShowsSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture);
        if (time == ClockTime) return;

        ClockTime = time;
        ClockDate = IsVertical
            ? now.ToString("d", CultureInfo.CurrentCulture)
            : now.ToString("ddd d MMM", CultureInfo.CurrentCulture);

        OnPropertyChanged(nameof(ClockTime));
        OnPropertyChanged(nameof(ClockDate));
    }

    private int _loggedTrayChanges;
    private readonly HashSet<string> _loggedTrayIdentities = [];

    private void LogTrayContents(string reason)
    {
        if (_notificationArea is null) return;

        LogNewTrayIdentities();

        // Only the first few of the running totals, or a chatty tray writes to disk forever. The
        // identity lines above are deduplicated instead, so they can keep running all session.
        if (_loggedTrayChanges++ > 6) return;

        int all = _notificationArea.TrayIcons.Count;
        int pinned = _notificationArea.PinnedIcons.Cast<object>().Count();
        int unpinned = _notificationArea.UnpinnedIcons.Cast<object>().Count();

        BringUpLog.Write($"tray {reason}: {all} total, {pinned} pinned, {unpinned} unpinned");

        foreach (NotifyIcon icon in _notificationArea.TrayIcons.Take(12))
            BringUpLog.Write($"  tray icon | pinned={icon.IsPinned} hidden={icon.IsHidden} " +
                             $"| {icon.Title} | {icon.Path}");
    }

    /// <summary>
    /// Writes one line per notification icon the session has never seen, naming its GUID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the instrument for the missing network indicator.</b> <c>NETWORK_GUID</c> is
    /// already in the pinned list, so an Ethernet or Wi-Fi icon that arrived would land in the
    /// right place by itself — the question is only whether one ever arrives, and on this machine
    /// none has. That is not the same as Windows 11 not sending system indicators, because the
    /// volume icon does arrive carrying <c>VOLUME_GUID</c>. Before concluding the network icon is
    /// part of the XAML Quick Settings cluster rather than a notify icon, the honest test is to
    /// watch every GUID for a while across a Wi-Fi/Ethernet change.
    /// </para>
    /// <para>
    /// Deduplicated by identity rather than capped by count, because the interesting event is
    /// rare and arrives long after the first few collection changes — a cap would switch the
    /// instrument off before the thing it exists to catch could happen.
    /// </para>
    /// <para>
    /// Icons are keyed by GUID where they have one and by path otherwise: a third of the icons
    /// measured on this machine carry all zeroes, so the GUID alone would collapse them into one
    /// entry and report the first arrival only.
    /// </para>
    /// </remarks>
    private void LogNewTrayIdentities()
    {
        if (_notificationArea is null) return;

        foreach (NotifyIcon icon in _notificationArea.TrayIcons)
        {
            string guid = icon.GUID.ToString();
            if (!_loggedTrayIdentities.Add($"{guid}|{icon.Path}|{icon.Title}")) continue;

            string known = SystemIndicatorName(guid);

            BringUpLog.Write(
                $"tray icon seen | guid={guid}{known} | pinned={icon.IsPinned} " +
                $"| {icon.Title} | {icon.Path}");
        }
    }

    /// <summary>Names the GUID when it is one of Windows' own indicators.</summary>
    private static string SystemIndicatorName(string guid)
    {
        if (Matches(NotificationArea.VOLUME_GUID)) return " (volume)";
        if (Matches(NotificationArea.NETWORK_GUID)) return " (network)";
        if (Matches(NotificationArea.POWER_GUID)) return " (power)";
        if (Matches(NotificationArea.HEALTH_GUID)) return " (health)";

        return string.Empty;

        bool Matches(string known) => string.Equals(guid, known, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Closes the Start menu before opening one of ours, which is what the click should have done.
    /// </summary>
    /// <remarks>
    /// See <see cref="StartMenuWatcher.Dismiss"/> for why this is a dismissal rather than a
    /// Z-order fight. The glyph colour is reset here because the watcher does not own it: leaving
    /// it would hold the Start button Windows-blue with no menu behind it.
    /// </remarks>
    private void DismissStartMenu()
    {
        if (!_startMenu.Dismiss()) return;

        StartActive = false;
        _startPressedAt = DateTime.MinValue;
    }

    /// <summary>
    /// Builds the slot's menu as it opens: the application's jump list tasks, then the application
    /// itself, then pin and close.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built rather than declared, because most of it is not knowable until the menu is asked for.
    /// The tasks come from what the application published to its jump list on disk, and the close
    /// item has to name how many windows it is about to close.
    /// </para>
    /// <para>
    /// The order is the Windows taskbar's: tasks at the top, furthest from the pointer, and the
    /// destructive item at the bottom. <b>Close is last on purpose</b> — it is the only item here
    /// that cannot be undone, and the pointer arrives at the end nearest the bar.
    /// </para>
    /// </remarks>
    private void OnSlotContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSlot slot } element) return;
        if (element.ContextMenu is not { } menu) return;

        menu.Items.Clear();

        AddTasks(menu, slot);

        // The application itself, which launches another copy. Windows puts this directly above
        // pin and close, labelled with the application's name rather than "Open" or "Run".
        AddItem(menu, slot.Pin?.Name ?? slot.DisplayName, slot.Icon, (_, _) => Launch(slot));

        AddItem(
            menu,
            slot.IsPinned ? "Unpin from bar" : "Pin to bar",
            icon: null,
            (s2, e2) => OnSlotPinClick(s2, e2),
            enabled: slot.IsPinned || slot.IsRunning);

        if (!slot.IsRunning) return;

        // Naming the count matters: "Close" on an application with six windows open is a much
        // bigger action than it looks, and the menu should say so before the click rather than
        // after it. Per-window close lives in the hover preview.
        menu.Items.Add(Separator());

        AddItem(
            menu,
            slot.Windows.Count > 1 ? $"Close all {slot.Windows.Count} windows" : "Close window",
            icon: null,
            (s2, e2) => OnSlotClose(s2, e2));
    }

    /// <summary>Adds the application's published jump list tasks, if it publishes any.</summary>
    /// <remarks>
    /// Silent when there are none, which is the common case for applications that never called
    /// <c>ICustomDestinationList</c> — an empty "Tasks" heading over nothing would be worse than no
    /// heading. The list is read from disk on the way past; see <see cref="JumpListIndex"/> for why
    /// that is affordable here.
    /// </remarks>
    private void AddTasks(ContextMenu menu, TaskbarSlot slot)
    {
        IReadOnlyList<JumpListTask> tasks;

        try
        {
            tasks = _jumpLists.TasksFor(slot.Key, slot.ExecutablePath);
        }
        catch (Exception ex)
        {
            // A menu that fails to open is far worse than one missing its tasks, and everything
            // below this point works without them.
            BringUpLog.Write(ex, $"jump list: could not read tasks for {slot.Key}");
            return;
        }

        if (tasks.Count == 0) return;

        AddHeader(menu, "Tasks");

        foreach (JumpListTask task in tasks.Take(MaximumJumpListTasks))
        {
            JumpListTask captured = task;
            AddItem(menu, task.Title, slot.Icon, (_, _) => RunTask(captured));
        }

        menu.Items.Add(Separator());
    }

    /// <summary>
    /// How many jump list entries the menu will show.
    /// </summary>
    /// <remarks>
    /// Windows shows about ten. The cap is not really about space: the scan cannot tell which
    /// category an entry belongs to, so a long list would mix an application's tasks with whatever
    /// else it publishes, and the useful items are at the top.
    /// </remarks>
    private const int MaximumJumpListTasks = 10;

    /// <summary>Runs a jump list task.</summary>
    /// <remarks>
    /// <c>UseShellExecute</c>, because a task's target is not always an ordinary executable a
    /// process can be started from directly — packaged applications live under <c>WindowsApps</c>,
    /// where direct execution is refused, and the shell knows how to launch them.
    /// </remarks>
    private static void RunTask(JumpListTask task)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(task.Path)
            {
                Arguments = task.Arguments,
                UseShellExecute = true,
            });

            BringUpLog.Write($"jump list task: {task.Title} -> {task.Path} {task.Arguments}");
        }
        catch (Exception e)
        {
            // Every exception, not a chosen few. A task that silently does nothing is the failure
            // mode this project has already been bitten by once.
            BringUpLog.Write(e, $"jump list task failed: {task.Title}");
        }
    }

    private void AddHeader(ContextMenu menu, string text) =>
        menu.Items.Add(new MenuItem { Header = text, Style = MenuStyle("MenuSectionHeader") });

    private void AddItem(
        ContextMenu menu, string header, ImageSource? icon, RoutedEventHandler onClick, bool enabled = true)
    {
        MenuItem item = new()
        {
            Header = header,
            IsEnabled = enabled,
            Style = MenuStyle("DarkMenuItem"),
        };

        if (icon is not null)
            item.Icon = new Image { Source = icon, Width = 16, Height = 16 };

        item.Click += onClick;
        menu.Items.Add(item);
    }

    private Separator Separator() => new() { Style = MenuStyle("MenuSeparatorStyle") };

    /// <summary>
    /// Looks a menu style up from the window, not from the menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A ContextMenu cannot find the window's resources.</b> It lives in its own popup tree and
    /// its resource lookup does not climb into the window that owns it, so
    /// <c>menu.TryFindResource</c> returns null for everything in <c>MenuTheme.xaml</c> — silently,
    /// because a null Style is legal and simply leaves WPF's default template in place.
    /// </para>
    /// <para>
    /// The result looked <i>almost</i> right, which is what made it hard to see: the default
    /// template also has an icon column and also highlights on hover, so the menu appeared themed
    /// while ignoring every metric in it. The give-aways were rows measuring 22 DIP against the
    /// style's 36, and separators running edge to edge instead of inset.
    /// </para>
    /// <para>
    /// Logged when a key goes missing, because the failure is invisible otherwise: the menu would
    /// keep opening, keep working, and quietly stop looking like itself.
    /// </para>
    /// </remarks>
    private Style? MenuStyle(string key)
    {
        if (TryFindResource(key) is Style style) return style;

        if (_missingMenuStyles.Add(key))
            BringUpLog.Write($"menu style '{key}' not found; falling back to the WPF default");

        return null;
    }

    private readonly HashSet<string> _missingMenuStyles = [];

    /// <summary>A panel Umbra can put on screen. One at a time, as on the Windows taskbar.</summary>
    private enum BarPanel
    {
        /// <summary>None of ours: the click belongs to another process.</summary>
        None,

        /// <summary>The notification-area overflow flyout.</summary>
        TrayOverflow,

        /// <summary>The window picker, whether raised by hovering an icon or by clicking one.</summary>
        Picker,

        /// <summary>Any of the bar's context menus.</summary>
        ContextMenu,
    }

    /// <summary>
    /// Closes every panel of ours except the one that is about to open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this they stack: the overflow flyout over an open jump list, a window preview over
    /// the flyout. Each has its own dismissal rule — the flyout and the picker watch the pointer,
    /// a menu watches for a click elsewhere — and none of those rules fires when the thing that
    /// covered it is another panel of ours, because the pointer never left the bar and the click
    /// landed inside Umbra.
    /// </para>
    /// <para>
    /// Windows' own surfaces are <b>not</b> closed here. That is
    /// <see cref="DismissWindowsSurfaces"/>, because it is only right for a panel the user
    /// deliberately opened — see the note there about hovering.
    /// </para>
    /// </remarks>
    private void ClearPanelsFor(BarPanel opening)
    {
        if (opening != BarPanel.TrayOverflow && TrayOverflowPopup.IsOpen)
            SetTrayOverflowOpen(false);

        if (opening != BarPanel.Picker)
        {
            _picker?.Dismiss();
            _picker = null;
        }

        if (opening != BarPanel.ContextMenu) _menuDismisser.Close();
    }

    /// <summary>
    /// Closes the Start menu and whichever of Windows' own flyouts is open, before one of ours
    /// opens over it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are things the click should have done by itself and did not: the bar carries
    /// <c>WS_EX_NOACTIVATE</c>, so clicking it never moves the foreground and neither surface is
    /// ever told it lost anything. See <see cref="StartMenuWatcher.Dismiss"/> and
    /// <see cref="ShellSurfaceWatcher"/>.
    /// </para>
    /// <para>
    /// <b>Called for a click and never for a hover.</b> Closing the Start menu because the pointer
    /// crossed the bar on its way somewhere else would be the bar reacting to traffic rather than
    /// to the user — the same reasoning that sets the engaged band to zero rather than §3's 40 px.
    /// So the hover preview clears our own panels and leaves Windows' alone, even though it means
    /// that one preview can be drawn underneath an open Start menu.
    /// </para>
    /// </remarks>
    private void DismissWindowsSurfaces()
    {
        DismissStartMenu();
        _shellSurfaces.Dismiss();
    }

    private readonly ShellSurfaceWatcher _shellSurfaces = new();

    /// <summary>Closes another application's menu when the user clicks the bar.</summary>
    private readonly Rendering.ForeignMenuDismisser _foreignMenus = new();

    /// <summary>
    /// Remembers the window a tray icon's owner puts up, so a press on the bar can close it.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed: Discord's "menu" is an ordinary Chromium top-level window,
    /// not a Win32 menu, so nothing generic recognises it. See <see cref="Rendering.TrayMenuWatcher"/>.
    /// </remarks>
    private readonly Rendering.TrayMenuWatcher _trayMenus = new();

    /// <summary>
    /// A press anywhere on the bar closes whatever is open over it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case every other dismissal path missed.</b> Clicking an icon, the chevron
    /// or the show-desktop sliver each closed Windows' panels, because each of those has a
    /// handler that says so. Clicking <i>empty space on the bar</i> ran no code at all, so the
    /// notification centre, Quick Settings and a tray icon's own menu all stayed open — while
    /// clicking the same empty space with our own context menu up closed that, because the one
    /// handler here did call the menu dismisser. Hence the report that right-click menus were
    /// fine and the other three were not.
    /// </para>
    /// <para>
    /// <b>The controls tagged <c>ShellToggle</c> are excluded, and that exclusion is the whole
    /// subtlety.</b> The clock, the volume and network cluster, the overflow chevron and the tray
    /// icons all raise a panel by sending its <i>toggle</i>. Dismissing on the way down would
    /// close the panel a moment before their own handler reopens it, or reopen one the user was
    /// closing. They own their dismissal; everything else on the bar delegates it here.
    /// </para>
    /// </remarks>
    private void OnBarPressed(object sender, MouseButtonEventArgs e)
    {
        // A press inside one of our own panels belongs to that panel, not to the bar underneath.
        if (PointerIsOnOwnPanel()) return;

        if (OwnsShellDismissal(e.OriginalSource as DependencyObject)) return;

        // Everything the bar has open, including the overflow flyout and the window picker.
        //
        // <b>This is the case the report was actually about, and it went unfixed for several
        // rounds because "tray menu" was read as an application's menu rather than as ours.</b>
        // Clicking the chevron opens our flyout; the only thing that closed it was the watcher
        // that fires 600 ms after the pointer leaves <i>both</i> the flyout and the bar. So the
        // pointer sitting anywhere on the bar kept it open indefinitely, and the user had to
        // click off the bar entirely — which is exactly what was described.
        ClearPanelsFor(BarPanel.None);

        _trayMenus.Close();
        DismissWindowsSurfaces();
    }

    /// <summary>Whether a press landed on a control that raises a shell surface itself.</summary>
    private static bool OwnsShellDismissal(DependencyObject? source)
    {
        for (DependencyObject? node = source; node is not null; node = VisualTreeHelperParent(node))
            if (node is FrameworkElement { Tag: "ShellToggle" }) return true;

        return false;
    }

    /// <summary>
    /// The visual parent, falling back to the logical one.
    /// </summary>
    /// <remarks>
    /// A press inside a <c>ContextMenu</c> or a <c>Popup</c> originates in a separate visual tree
    /// whose root has no visual parent, so walking visuals alone stops at the popup and never
    /// reaches the control that owns it.
    /// </remarks>
    private static DependencyObject? VisualTreeHelperParent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    /// <summary>
    /// Keeps the chevron and an open flyout honest when icons arrive or leave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An application can register or drop a notification icon at any moment, including while its
    /// panel is on screen — a download finishing, an update agent exiting. Two things go wrong if
    /// nothing is done about it.
    /// </para>
    /// <para>
    /// <b>The panel grows from its top-left corner.</b> A popup keeps the placement it was given,
    /// so a row appearing makes it taller and it extends <i>downward</i>, through the bar, rather
    /// than staying centred above the chevron. <c>Reposition</c> runs the placement callback
    /// again against the new size; at <c>Loaded</c> priority, because the size is not final until
    /// the new item has been measured.
    /// </para>
    /// <para>
    /// <b>The last icon leaving takes the chevron with it</b>, since the chevron is collapsed when
    /// there is nothing behind it. That would leave the flyout floating above a gap, anchored to
    /// an element with no position, until the pointer wandered off and the grace period ran out.
    /// </para>
    /// </remarks>
    private void OnTrayIconsChanged()
    {
        OnPropertyChanged(nameof(TrayOverflowVisibility));

        if (!TrayOverflowPopup.IsOpen) return;

        if (TrayOverflowVisibility != Visibility.Visible)
        {
            SetTrayOverflowOpen(false);
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (TrayOverflowPopup.IsOpen) Reposition(TrayOverflowPopup);
        });
    }

    /// <summary>
    /// Makes a popup run its placement again.
    /// </summary>
    /// <remarks>
    /// <c>Popup.Reposition</c> is exactly this method and is internal to WPF, so the way in is to
    /// change a property that placement depends on and change it straight back. The value itself
    /// does not matter here: <see cref="PlaceTrayOverflow"/> is handed the offset and ignores it,
    /// deriving everything from the two sizes.
    /// </remarks>
    private static void Reposition(System.Windows.Controls.Primitives.Popup popup)
    {
        double offset = popup.HorizontalOffset;

        popup.HorizontalOffset = offset + 1;
        popup.HorizontalOffset = offset;
    }

    /// <summary>Opens or closes the overflow flyout.</summary>
    private void OnTrayChevronClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        PlayPress(TrayChevronScale);

        bool opening = !TrayOverflowPopup.IsOpen;

        if (opening)
        {
            ClearPanelsFor(BarPanel.TrayOverflow);

            // Before the popup is placed, not after. Dismissing a surface once ours is already up
            // shows it clipped for as long as the other one takes to animate away — the same
            // ordering the context menus need against the Start menu.
            DismissWindowsSurfaces();
        }

        SetTrayOverflowOpen(opening);
    }

    /// <summary>
    /// Centres the flyout on the chevron, on the inboard side of whichever edge the bar is on.
    /// </summary>
    /// <remarks>
    /// Evaluated every time the popup opens, so it follows the bar when the user moves it between
    /// edges without a restart. The geometry itself is in <see cref="FlyoutPlacement"/>, which has
    /// no WPF state to set up and so can be tested without a window.
    /// </remarks>
    private CustomPopupPlacement[] PlaceTrayOverflow(Size popupSize, Size targetSize, Point offset) =>
    [
        new CustomPopupPlacement(
            FlyoutPlacement.Centred(Metrics.Edge, popupSize, targetSize),
            FlyoutPlacement.SlideAxis(Metrics.Edge)),
    ];

    /// <summary>
    /// Shows or hides the flyout, and runs the pointer watcher only while it is up.
    /// </summary>
    /// <remarks>
    /// The watcher is the same device the window picker and the context menus use, for the same
    /// reason: the bar carries <c>WS_EX_NOACTIVATE</c>, so WPF's own dismiss-on-click-outside —
    /// which works by capturing the mouse — is not honoured across processes. Without this the
    /// flyout stays open until you click back inside Umbra.
    /// </remarks>
    private void SetTrayOverflowOpen(bool open)
    {
        TrayOverflowPopup.IsOpen = open;

        if (!open)
        {
            _trayOverflowWatch?.Stop();
            return;
        }

        _trayOverflowWatch ??= CreateTrayOverflowWatch();

        // The button that opened the flyout is still down. Seeding the watch with that fact is
        // what stops its own opening press being read as the click that dismisses it.
        _trayOverflowPressWasDown = true;
        _trayOverflowWatch.Start();

        LogTrayOverflowPlacement();
    }

    /// <summary>
    /// Records where the flyout actually landed, against the chevron it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The placement arithmetic is asserted in <c>FlyoutPlacementTests</c>; what a test cannot
    /// reach is whether WPF handed the callback the sizes it claims to, and whether Windows then
    /// pushed the result somewhere else to keep it on screen. Both show up here as a centre error
    /// in pixels.
    /// </para>
    /// <para>
    /// It also means the other three edges can be checked from the log rather than from a
    /// screenshot taken at exactly the right moment — the flyout closes 600 ms after the pointer
    /// leaves it, and a synthetic pointer is not reliable enough to hold it open (see the
    /// methodological note in docs/measurements.md).
    /// </para>
    /// <para>
    /// At <c>Loaded</c> priority: the popup has no window rectangle until it has been placed, and
    /// reading one before that reports the origin.
    /// </para>
    /// </remarks>
    private void LogTrayOverflowPlacement() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (!TrayOverflowPopup.IsOpen) return;

        if (TrayOverflowPopup.Child is not FrameworkElement child
            || PresentationSource.FromVisual(child) is not HwndSource source
            || !TrayNative.GetWindowRect(source.Handle, out TrayNative.RECT popup))
        {
            BringUpLog.Write("tray overflow open: no window rectangle to measure");
            return;
        }

        // Both in physical pixels: the popup's from Win32, the chevron's through PointToScreen,
        // which returns physical. Nothing here is in DIP, so nothing needs converting.
        Point origin = TrayChevron.PointToScreen(new Point(0, 0));
        Point far = TrayChevron.PointToScreen(
            new Point(TrayChevron.ActualWidth, TrayChevron.ActualHeight));

        double chevronCentreX = (origin.X + far.X) / 2;
        double chevronCentreY = (origin.Y + far.Y) / 2;
        double popupCentreX = (popup.Left + popup.Right) / 2.0;
        double popupCentreY = (popup.Top + popup.Bottom) / 2.0;

        BringUpLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"tray overflow open on {Metrics.Edge}: popup {popup.Left},{popup.Top} " +
            $"{popup.Right - popup.Left}x{popup.Bottom - popup.Top} | " +
            $"chevron centre {chevronCentreX:F0},{chevronCentreY:F0} | " +
            $"offset along bar {(IsVertical ? popupCentreY - chevronCentreY : popupCentreX - chevronCentreX):F1} px"));
    });

    private DispatcherTimer CreateTrayOverflowWatch()
    {
        DispatcherTimer timer = new(DispatcherPriority.Background)
        {
            // 10 Hz, and only while the flyout is open. §6.6 is about not adding standing
            // wake-ups; this one exists for as long as a panel the user opened is on screen.
            Interval = TimeSpan.FromMilliseconds(100),
        };

        // <b>Closed by a click away from it, not by the pointer leaving it.</b> It used to close
        // 600 ms after the pointer was no longer over the flyout or the chevron, which made it
        // vanish a second or two after opening if the user did not keep the pointer on it —
        // reported as exactly that. Windows' own overflow stays up until you click elsewhere,
        // and so does every other panel the bar has.
        //
        // A press anywhere inside Umbra is handled in OnBarPressed, which has the event in hand;
        // this watches for the presses we never see, which is every one outside our windows.
        timer.Tick += (_, _) =>
        {
            if (!TrayOverflowPopup.IsOpen)
            {
                timer.Stop();
                _trayOverflowPressWasDown = false;
                return;
            }

            bool down = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0
                     || (GetAsyncKeyState(VkRightButton) & 0x8000) != 0
                     || (GetAsyncKeyState(VkMiddleButton) & 0x8000) != 0;

            bool pressed = down && !_trayOverflowPressWasDown;
            _trayOverflowPressWasDown = down;

            // The edge, not the state — otherwise the press that opens the flyout is still down
            // on the next tick and closes it again immediately.
            if (!pressed) return;
            if (PointerIsOverTrayOverflow()) return;

            SetTrayOverflowOpen(false);
        };

        return timer;
    }

    /// <summary>
    /// Whether the pointer is on any panel the bar has open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each panel answers for itself, in its own units — the flyout and the menus in physical
    /// pixels straight from Win32, the picker in device-independent ones against its own window.
    /// Nothing is converted here, which is the point: this project has lost a day to mixing those
    /// two before.
    /// </para>
    /// <para>
    /// Cheap enough for the 16 ms tick. Each test is a cursor read and a rectangle comparison, and
    /// all of them short-circuit when nothing is open.
    /// </para>
    /// </remarks>
    private bool PointerIsOnOwnPanel()
    {
        if (TrayOverflowPopup.IsOpen && PointerIsOverTrayOverflow()) return true;
        if (_picker?.PointerIsInside() == true) return true;

        return _menuDismisser.PointerIsOnMenu();
    }

    /// <summary>Whether the cursor is on the flyout or on the chevron that opened it.</summary>
    /// <remarks>
    /// Both rectangles are read in physical pixels straight from Win32, and the cursor is too, so
    /// no DPI conversion happens anywhere in the comparison — the same rule the context menu
    /// dismisser follows.
    /// </remarks>
    private bool PointerIsOverTrayOverflow()
    {
        if (!TrayNative.GetCursorPos(out TrayNative.POINT cursor)) return true;

        if (TrayOverflowPopup.Child is FrameworkElement child
            && PresentationSource.FromVisual(child) is HwndSource source
            && TrayNative.GetWindowRect(source.Handle, out TrayNative.RECT popup)
            && cursor.X >= popup.Left && cursor.X < popup.Right
            && cursor.Y >= popup.Top && cursor.Y < popup.Bottom)
        {
            return true;
        }

        Point origin = TrayChevron.PointToScreen(new Point(0, 0));
        Point far = TrayChevron.PointToScreen(new Point(TrayChevron.ActualWidth, TrayChevron.ActualHeight));

        return cursor.X >= origin.X && cursor.X < far.X && cursor.Y >= origin.Y && cursor.Y < far.Y;
    }

    private DispatcherTimer? _trayOverflowWatch;

    /// <summary>Whether a mouse button was already down on the previous overflow-watch tick.</summary>
    private bool _trayOverflowPressWasDown;

    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;
    private const int VkMiddleButton = 0x04;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static class TrayNative
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    }

    /// <summary>Starts the hover-preview countdown for the icon under the pointer.</summary>
    /// <remarks>
    /// Moving from one icon to another while a preview is already up switches immediately rather
    /// than waiting again: the user is demonstrably in preview mode, and re-imposing the delay
    /// makes the row feel sticky.
    /// </remarks>
    private void OnSlotMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSlot slot } element) return;

        _hoverSlot = slot;
        _hoverElement = element;
        _hoverTimer?.Stop();

        if (!slot.IsRunning || _dragging) return;

        if (_picker is not null)
        {
            if (!AppIdentity.Same(_picker.SlotKey, slot.Key)) ShowPreview(slot, element);
            return;
        }

        _hoverTimer?.Start();
    }

    /// <summary>
    /// Cancels a pending preview when the pointer leaves.
    /// </summary>
    /// <remarks>
    /// Deliberately does not dismiss a preview that is already up. The pointer has to cross the gap
    /// between the bar and the picker to reach a thumbnail, and dismissing on leave would close it
    /// mid-journey. The picker watches the cursor itself and closes once it is away from both.
    /// </remarks>
    private void OnSlotMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer?.Stop();
        _hoverSlot = null;
        _hoverElement = null;
    }

    /// <summary>
    /// Records a press, so a later move can tell a drag from a click.
    /// </summary>
    /// <remarks>
    /// Any icon reorders, pinned or not. The row is one order covering both (see
    /// <see cref="Tasks.BarOrder"/>), so there is no longer a running tail without a stored order
    /// for a drag to rewrite — which is the only reason unpinned icons used to be refused.
    /// </remarks>
    private void OnSlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSlot slot } element) return;

        // One icon has nowhere to go.
        if (Slots.Count < 2) return;

        _pressedSlot = slot;
        _pressedElement = element;
        _pressedAt = e.GetPosition(WindowList);
        _dragging = false;
    }

    /// <summary>
    /// Turns a press plus movement along the bar into a live reorder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pointer has to travel the system drag threshold before this commits to a drag, so an
    /// ordinary click with a shaky hand still activates the application rather than silently
    /// rearranging the user's taskbar.
    /// </para>
    /// <para>
    /// Reordering goes through <see cref="TaskbarModel"/> rather than moving items in the
    /// collection directly. The model rebuilds itself from its own order, so a direct collection
    /// move would be reverted the next time any window anywhere opened or closed — visibly, and
    /// only sometimes, which is the worst kind of bug to be handed.
    /// </para>
    /// <para>
    /// The dragged icon is carried under the pointer while the rest of the row slides around it.
    /// Snapping it from slot to slot instead was the first cut, and it made a drag hard to aim:
    /// nothing said which icon was being moved except that it had dimmed.
    /// </para>
    /// </remarks>
    private void OnSlotMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedSlot is null || e.LeftButton != MouseButtonState.Pressed) return;

        Point now = e.GetPosition(WindowList);
        double along = IsVertical ? now.Y : now.X;

        if (!_dragging)
        {
            double moved = Math.Abs(along - (IsVertical ? _pressedAt.Y : _pressedAt.X));

            double threshold = IsVertical
                ? SystemParameters.MinimumVerticalDragDistance
                : SystemParameters.MinimumHorizontalDragDistance;

            if (moved < threshold) return;

            _dragging = true;
            _pressedElement?.CaptureMouse();
            SetDragFeedback(_pressedElement, dragging: true);
            BeginCarry();

            // A preview left hanging over the row would sit on top of the thing being dragged.
            _hoverTimer?.Stop();
            _picker?.Dismiss();
            _picker = null;
        }

        // Which slot position the pointer is over, in the undrifted layout (P5: hit geometry does
        // not move, so this needs no drift correction).
        int target = Math.Clamp((int)Math.Floor(along / Metrics.Slot.Pitch), 0, Slots.Count - 1);

        _tasks.Move(_pressedSlot.Key, target, persist: false);
        Carry(along);
    }

    /// <summary>
    /// Lifts the pressed icon out of the row's own animation so the pointer can carry it.
    /// </summary>
    /// <remarks>
    /// The panel arranges item containers, not the template's hit slot, so what gets carried is the
    /// container. It is the same instance for the whole drag — the model moves slots rather than
    /// replacing them, and WPF's generator moves the matching container rather than rebuilding it —
    /// so it is looked up once, on the frame the drag commits.
    /// </remarks>
    private void BeginCarry()
    {
        _row ??= FindDescendant<SlidingStackPanel>(WindowList);
        if (_row is null || _pressedElement is null) return;

        _dragContainer = WindowList.ContainerFromElement(_pressedElement) as UIElement;
        if (_dragContainer is null) return;

        double arranged = SlidingStackPanel.ArrangedAt(_dragContainer);

        if (double.IsNaN(arranged))
        {
            // Never arranged, so there is no frame of reference to carry it in. Reorder without it.
            _dragContainer = null;
            return;
        }

        // Where within the slot the icon was taken hold of, so it does not jump to centre itself
        // under the pointer the instant the drag commits.
        _grabAlong = (IsVertical ? _pressedAt.Y : _pressedAt.X) - arranged;
        _row.Hold(_dragContainer);
    }

    /// <summary>Draws the held icon under the pointer.</summary>
    private void Carry(double along)
    {
        if (_row is null || _dragContainer is null) return;

        double arranged = SlidingStackPanel.ArrangedAt(_dragContainer);
        if (double.IsNaN(arranged)) return;

        // Following the pointer, but not off the end of the row: an overshoot should rest against
        // the last slot rather than trail the icon out over the clock.
        double limit = Math.Max(0, (Slots.Count - 1) * Metrics.Slot.Pitch);
        double desired = Math.Clamp(along - _grabAlong, 0, limit);

        _row.Carry(_dragContainer, desired - arranged);
    }

    /// <summary>
    /// Cleans up if capture is taken away mid-drag, so the icon cannot be left dimmed and the
    /// order the user had reached is still written.
    /// </summary>
    private void OnSlotLostCapture(object sender, MouseEventArgs e)
    {
        if (_pressedSlot is not null) EndDrag();
    }

    /// <summary>Ends a drag, writing the order the user settled on.</summary>
    /// <remarks>
    /// State is cleared <b>before</b> capture is released. <c>ReleaseMouseCapture</c> raises
    /// <c>LostMouseCapture</c> synchronously, which calls straight back into here — so releasing
    /// first would run this whole method twice, persisting and logging the reorder twice on every
    /// drop. It resolved correctly by accident, which is a worse failure than not working.
    /// </remarks>
    private void EndDrag()
    {
        FrameworkElement? element = _pressedElement;
        UIElement? container = _dragContainer;
        bool dragged = _dragging;

        _pressedSlot = null;
        _pressedElement = null;
        _dragContainer = null;
        _dragging = false;

        // Hands the icon back to the panel, which slides it the last few pixels into its slot
        // rather than letting it snap there the moment the button comes up.
        if (container is not null) _row?.Release(container);

        if (element is not null)
        {
            element.ReleaseMouseCapture();
            SetDragFeedback(element, dragging: false);
        }

        if (!dragged) return;

        _tasks.PersistOrder();
        BringUpLog.Write($"reordered row: {string.Join(", ", Slots.Select(s => s.Pin?.Name ?? s.Key))}");
    }

    /// <summary>Lifts the icon being dragged, so it reads as picked up rather than as left behind.</summary>
    /// <remarks>
    /// A light touch, not the near-transparency this used to be. The icon now travels with the
    /// pointer, so the movement already says it has been picked up; fading it to 0.45 on top of
    /// that read as the icon disappearing rather than as it being held.
    /// </remarks>
    private static void SetDragFeedback(FrameworkElement? slotElement, bool dragging)
    {
        if (slotElement is null) return;
        if (FindDescendant<Image>(slotElement) is not { } icon) return;

        icon.Opacity = dragging ? 0.7 : 1.0;
    }

    /// <summary>
    /// Click a slot to activate its window, or to minimise it if it is already in front — the
    /// behaviour the Windows taskbar has.
    /// </summary>
    private void OnSlotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSlot slot } element) return;

        e.Handled = true;

        // The mouse-up that ends a drag is delivered here, to the element that held capture.
        // Letting it fall through would activate whatever the icon was dropped on.
        bool wasDragging = _dragging;
        EndDrag();
        if (wasDragging) return;

        BringUpLog.Write($"slot click: index {_tasks.Slots.IndexOf(slot)} | {slot.Key} | " +
                         $"{slot.Windows.Count} windows | running={slot.IsRunning}");
        PlayIconPress(element);

        // A click on the row is the user choosing the bar over whatever Windows has open, and the
        // real taskbar closes Quick Settings and the notification centre when you click a button
        // on it. Neither closes itself for us: the bar carries WS_EX_NOACTIVATE, so clicking it
        // never moves the foreground and neither surface is ever told it lost anything.
        //
        // <b>This is not only cosmetic, and that is what made it look like two bugs.</b> While one
        // of those panels holds the foreground, our activation of the user's own window is refused
        // by the foreground lock — so the panel stayed up *and* the icons underneath appeared
        // dead. Reported exactly that way. Unlike the tray icons and the clock, an application
        // icon is not a toggle for these panels, so dismissing first cannot undo itself.
        DismissWindowsSurfaces();

        // Nothing running: this is a pinned application, so start it.
        if (!slot.IsRunning)
        {
            Launch(slot);
            return;
        }

        // Several windows: show them rather than guessing which one was meant. This replaces the
        // cycle-to-next this handler used to do, which gave no way to reach a particular window
        // except by clicking until it came up.
        if (slot.Windows.Count > 1)
        {
            TogglePicker(slot, element);
            return;
        }

        // Asking Windows directly rather than trusting ManagedShell's State bookkeeping: State is
        // maintained from foreground-change events, and a click that arrives mid-transition can see
        // a stale value. The foreground window is the ground truth for "is this the one I am
        // looking at".
        IntPtr foreground = GetForegroundWindow();
        ApplicationWindow? active = slot.Windows.FirstOrDefault(w => w.Handle == foreground);

        if (active is null)
        {
            Activate(slot.Windows[0]);
            return;
        }

        // Already looking at it: minimise, as the Windows taskbar does.
        if (!active.IsMinimized) active.Minimize();
        else Activate(active);

        // The preview was of the window just acted on, so it has nothing left to show.
        _picker?.Dismiss();
        _picker = null;
    }

    /// <summary>
    /// Brings one of the user's own windows forward, past the foreground lock.
    /// </summary>
    /// <remarks>
    /// ManagedShell's <c>BringToFront</c> does the restore-and-activate correctly, but its
    /// <c>SetForegroundWindow</c> is subject to the same refusal every other one is: we are never
    /// the foreground process. Dismissing Windows' flyout first fixes the ordinary case, and that
    /// dismissal is asynchronous — the panel animates away — so the activation that follows can
    /// still land inside the lock. The attach in <see cref="WindowOrder.ForceForeground"/> does
    /// not care who holds the foreground, so it closes that window.
    /// </remarks>
    private static void Activate(ApplicationWindow window)
    {
        window.BringToFront();

        if (window.Handle != IntPtr.Zero) WindowOrder.ForceForeground(window.Handle);
    }

    /// <summary>
    /// Opens the window picker for a slot, or closes it if that slot's picker is already up.
    /// </summary>
    /// <remarks>
    /// Everything here is in DIP. <c>Visual.PointToScreen</c> would have been the obvious way to
    /// find the slot on screen and it returns <i>physical</i> pixels, while <see cref="Window.Left"/>
    /// and <see cref="Window.Top"/> are logical — mixing the two puts the picker a quarter of the
    /// screen away at 125% and dead centre at 100%, so it looks correct on the machine it was
    /// written on. Offsetting within the window and adding the window's own logical position keeps
    /// one unit system throughout.
    /// </remarks>
    private void TogglePicker(TaskbarSlot slot, FrameworkElement element)
    {
        if (_picker is not null)
        {
            bool sameSlot = AppIdentity.Same(_picker.SlotKey, slot.Key);

            // Already showing this application, most likely because hovering raised it. Leave it
            // up: the user's next click belongs to a thumbnail, not to the icon underneath.
            if (sameSlot) return;
        }

        // A click, unlike a hover, is the user choosing this panel over whatever Windows has open.
        // OnSlotClick dismisses for the same reason before it does anything else — it used to be
        // exempt here on the grounds that activating a window moves the foreground and closes
        // those surfaces by itself, which is exactly backwards: the foreground lock refuses that
        // activation for as long as the panel holds it.
        DismissWindowsSurfaces();

        ShowPreview(slot, element);
    }

    /// <summary>
    /// Raises the preview for a slot, replacing whatever was showing.
    /// </summary>
    /// <remarks>
    /// The same window serves the hover preview and the click-through picker, because they are the
    /// same thing: live thumbnails of everything this application has open. Splitting them would
    /// mean two DWM registration paths and two dismissal policies to keep in agreement.
    /// </remarks>
    private void ShowPreview(TaskbarSlot slot, FrameworkElement element)
    {
        if (!slot.IsRunning) return;
        if (_dragging) return;

        if (_picker is not null)
        {
            if (AppIdentity.Same(_picker.SlotKey, slot.Key)) return;
            _picker.Dismiss();
            _picker = null;
        }

        // The overflow flyout and any open menu belong to the panel the user has finished with.
        // Windows' own surfaces are left alone: this runs on hover too, and closing the Start menu
        // because the pointer crossed the bar is the bar reacting to traffic.
        ClearPanelsFor(BarPanel.Picker);

        Point inWindow = element.TransformToAncestor(this).Transform(new Point(0, 0));

        System.Windows.Rect anchor = new(
            Left + inWindow.X, Top + inWindow.Y, element.ActualWidth, element.ActualHeight);

        System.Windows.Rect bar = new(Left, Top, Width, Height);

        BringUpLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"picker open: {slot.Key} | {slot.Windows.Count} windows | " +
            $"anchor {anchor.Left:F1},{anchor.Top:F1} {anchor.Width:F0}x{anchor.Height:F0} DIP"));

        WindowPicker picker = new(slot, anchor, bar, Metrics.Edge, DpiScale <= 0 ? 1.0 : DpiScale);
        picker.Dismissed += () => _picker = null;

        _picker = picker;
        picker.Show();
    }

    /// <summary>
    /// Press feedback on an application icon: a quick dip and spring, the same shape as the Start
    /// button's.
    /// </summary>
    /// <remarks>
    /// Applied to the icon inside the slot rather than to the slot itself, so the hit rectangle
    /// stays exactly where it was (P5) — the same separation the drift transform uses. Costs
    /// nothing in wear terms: it runs for 260 ms while the pointer is necessarily on the bar, so
    /// the icon is at Tier 2 anyway, and nothing stays lit afterwards.
    /// </remarks>
    private static void PlayIconPress(FrameworkElement slotElement)
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        // The Image lives inside a DataTemplate, so it is reached through the visual tree rather
        // than by name — a template's names belong to its own scope, not the slot's.
        Image? icon = FindDescendant<Image>(slotElement);
        if (icon?.RenderTransform is not TransformGroup group) return;
        if (group.Children.FirstOrDefault() is not ScaleTransform scale) return;

        QuadraticEase down = new() { EasingMode = EasingMode.EaseOut };
        BackEase back = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };

        DoubleAnimationUsingKeyFrames Squash() => new()
        {
            KeyFrames =
            {
                new EasingDoubleKeyFrame(0.82, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)), down),
                new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), back),
            },
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Squash());
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Squash());
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;

            T? nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }

    private void Launch(TaskbarSlot slot)
    {
        (string Path, string Arguments)? target = slot.LaunchTarget;

        BringUpLog.Write($"launch: key={slot.Key} pin='{slot.Pin?.Path}' resolved='{target?.Path}'");

        try
        {
            // A resolved target is a real file on disk — either the pin's own executable or the
            // Start Menu shortcut's, once the pin's has gone. Anything else is an app user model
            // ID, which is how packaged applications start and cannot be run as a file.
            if (target is { } runnable)
            {
                bool started = ManagedShell.Common.Helpers.ShellHelper.StartProcess(
                    runnable.Path, runnable.Arguments, string.Empty, string.Empty);

                BringUpLog.Write($"launch: StartProcess returned {started}");
                if (started) return;
            }

            ManagedShell.Common.Helpers.ShellHelper.ActivateApplication(slot.Key, string.Empty);
            BringUpLog.Write("launch: went via ActivateApplication");
        }
        catch (Exception ex)
        {
            // Deliberately every exception, not a chosen few. A launch that throws anything else
            // was previously silent, which is the state this was found in.
            BringUpLog.Write(ex, $"failed to launch {slot.Key}");
        }
    }

    private void OnSlotPinClick(object sender, RoutedEventArgs e)
    {
        // A menu item built in code inherits its DataContext from the ContextMenu, which inherits
        // from the slot it was opened on. Same route the declared items used.
        if (sender is not FrameworkElement { DataContext: TaskbarSlot slot }) return;

        if (slot.IsPinned) Pins.Unpin(slot.Key);
        else if (slot.Windows.Count > 0) Pins.Pin(slot.Windows[0]);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>
    /// Stops the bar becoming the foreground window when it is clicked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, clicking a slot activates Umbra and the application loses foreground before
    /// the click handler runs — so "is this window already in front?" always answers no, and
    /// clicking the active window's icon re-activates it instead of minimising it. That is the bug
    /// this fixes, and it was invisible from the code: the handler reads correctly and simply never
    /// sees the state it is testing for.
    /// </para>
    /// <para>
    /// Real taskbars do the same. A taskbar that stole focus every time you clicked it would send
    /// the window you were typing in to the background on the way past.
    /// </para>
    /// </remarks>
    private void SuppressActivation()
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_NOACTIVATE = 0x08000000;

        if (Handle == IntPtr.Zero) return;

        long style = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE));
    }

    private void OnSlotClose(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSlot slot }) return;

        // Closing a group closes all of it, which is what the Windows taskbar does. Iterating a
        // copy: closing a window removes it from the slot's list as we go.
        //
        // Through WindowSafety, never ApplicationWindow.Close. This matters most here: a group
        // close is the one path that hands the shell process several windows at once, and anything
        // in an explorer.exe slot that is not a browser window is refused rather than closed.
        int asked = 0;
        int refused = 0;

        foreach (ApplicationWindow window in slot.Windows.ToArray())
        {
            if (WindowSafety.TryClose(window)) asked++;
            else refused++;
        }

        BringUpLog.Write($"group close: {slot.Key} | asked {asked}, refused {refused}");

        // The preview was of the windows just closed, so it has nothing left to show.
        _picker?.Dismiss();
        _picker = null;
    }

    /// <summary>
    /// Forwards a click on a tray icon back to the application that owns it.
    /// </summary>
    /// <remarks>
    /// The notification area is a proxy, not a replacement: the icon belongs to another process and
    /// only that process knows what clicking it means. ManagedShell repackages this into the
    /// callback message the owner registered for, which is why the screen position has to travel
    /// with it — that is where the owner will put its menu.
    /// </remarks>
    private void OnTrayIconMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NotifyIcon icon }) return;

        AllowIconOwnerForeground(icon);

        // Ours go now, before the owner is told: whatever it raises belongs in front of them, and
        // a jump list left open would be over the top of it.
        //
        // The flyout is exempt until the button comes up — it is named here as the panel that
        // stays — because the icon may be <i>inside</i> it. Closing it on the way down detaches
        // the element the button is on, and the mouse-up that most applications actually open
        // their menu from would never be delivered. It closes in OnTrayIconMouseUp instead, after
        // the click has been forwarded.
        //
        // Windows' own surfaces are deliberately not touched here, and this is the one place where
        // that matters: every one of those is a toggle. Closing Quick Settings and then handing
        // the click to the volume icon — which opens Quick Settings — would leave the panel up
        // when the user had just asked for it to go away.
        ClearPanelsFor(BarPanel.TrayOverflow);

        UpdateTrayHostSize();
        icon.IconMouseDown(e.ChangedButton, PackedCursorPosition(), DoubleClickTime);
        e.Handled = true;
    }

    private void OnTrayIconMouseUp(object sender, MouseButtonEventArgs e)
    {
        // A left click on an icon dismisses the panel it was in, or the flyout sits over whatever
        // window the click just raised.
        //
        // <b>A right click does not.</b> It opens the application's own menu beside the icon, and
        // taking the flyout away underneath it leaves that menu floating next to nothing —
        // reported against Steam. Windows' own overflow behaves the same way: the menu appears
        // and the panel stays. The flyout then closes on the next press outside it, which is the
        // press that dismisses the application's menu too.
        if (e.ChangedButton == MouseButton.Left && TrayOverflowPopup.IsOpen)
            SetTrayOverflowOpen(false);

        if (sender is not FrameworkElement { DataContext: NotifyIcon icon }) return;

        AllowIconOwnerForeground(icon);

        icon.IconMouseUp(e.ChangedButton, PackedCursorPosition(), DoubleClickTime);
        _trayMenus.Watch(icon.HWnd);
        e.Handled = true;
    }

    private void OnTrayIconMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NotifyIcon icon })
            icon.IconMouseEnter(PackedCursorPosition());
    }

    private void OnTrayIconMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NotifyIcon icon })
            icon.IconMouseLeave(PackedCursorPosition());
    }

    /// <summary>
    /// Lets a notification icon's owner take the foreground before it is told about the click.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes a tray menu behave.</b> An application putting up a menu from its
    /// icon must make its own window foreground first, or the menu does not dismiss when you
    /// click away and closes by itself a few seconds later — both reported here. The application
    /// makes that call and Windows refuses it, because nobody granted it the right: clicking our
    /// bar never moves the foreground, so there is nothing to hand on. Explorer's taskbar has the
    /// foreground to give; we have to borrow it. See <see cref="WindowOrder.AllowForeground"/>.
    /// </para>
    /// <para>
    /// Done on the way down as well as the way up, because applications differ on which one they
    /// raise the menu from, and granting twice costs nothing.
    /// </para>
    /// </remarks>
    private static void AllowIconOwnerForeground(NotifyIcon icon)
    {
        if (icon.HWnd != IntPtr.Zero) WindowOrder.AllowForeground(icon.HWnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();

    private static int DoubleClickTime => GetDoubleClickTime();

    /// <summary>Cursor position packed into a single value, as the tray callback protocol expects.</summary>
    private static uint PackedCursorPosition()
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
        return (uint)((cursor.y << 16) | (cursor.x & 0xFFFF));
    }

    /// <summary>
    /// Tells the notification area where it is on screen, so an application's flyout opens next to
    /// its icon rather than wherever Explorer's taskbar used to be (§11.1).
    /// </summary>
    private void UpdateTrayHostSize()
    {
        if (!IsLoaded || _notificationArea is null) return;

        try
        {
            GeneralTransform transform = TrayHost.TransformToAncestor(this);
            System.Windows.Rect bounds = transform.TransformBounds(
                new System.Windows.Rect(0, 0, TrayHost.ActualWidth, TrayHost.ActualHeight));

            double scale = DpiScale <= 0 ? 1.0 : DpiScale;

            _notificationArea.SetTrayHostSizeData(new TrayHostSizeData
            {
                edge = ToAppBarEdgeNative(Metrics.Edge),
                rc = new NativeMethods.Rect
                {
                    Left = (int)Math.Round((Left + bounds.Left) * scale),
                    Top = (int)Math.Round((Top + bounds.Top) * scale),
                    Right = (int)Math.Round((Left + bounds.Right) * scale),
                    Bottom = (int)Math.Round((Top + bounds.Bottom) * scale),
                },
            });
        }
        catch (InvalidOperationException)
        {
            // Not connected to the visual tree yet; the next click will set it.
        }
    }

    private static NativeMethods.ABEdge ToAppBarEdgeNative(CoreBarEdge edge) => edge switch
    {
        CoreBarEdge.Left => NativeMethods.ABEdge.ABE_LEFT,
        CoreBarEdge.Top => NativeMethods.ABEdge.ABE_TOP,
        CoreBarEdge.Right => NativeMethods.ABEdge.ABE_RIGHT,
        _ => NativeMethods.ABEdge.ABE_BOTTOM,
    };

    private static AppBarEdge ToAppBarEdge(CoreBarEdge edge) => edge switch
    {
        CoreBarEdge.Left => AppBarEdge.Left,
        CoreBarEdge.Top => AppBarEdge.Top,
        CoreBarEdge.Right => AppBarEdge.Right,
        CoreBarEdge.Bottom => AppBarEdge.Bottom,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };
}
