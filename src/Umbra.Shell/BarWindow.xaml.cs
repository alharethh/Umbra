using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        // Null when tray hosting is switched off, which is the default — see ShellHost. Pinned
        // rather than all: the unpinned ones belong behind an overflow chevron, which is §7.1's
        // "tray overflow flyout" and is not built yet.
        _notificationArea = host.ShellManager.NotificationArea;
        TrayIcons = _notificationArea?.PinnedIcons;

        DataContext = this;
        InitializeComponent();

        _proximity = new ProximityDriver(
            () => new System.Windows.Rect(Left, Top, Width, Height),
            () => DpiScale <= 0 ? 1.0 : DpiScale);

        _proximity.LevelChanged += level => TierLevel = level;

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

            bool justPressed = DateTime.UtcNow - _startPressedAt < StartPressGrace;
            StartActive = _startMenu.IsOpen || justPressed;

            // Which slot owns the foreground window, for the running indicator.
            _tasks.UpdateForeground(GetForegroundWindow());

            // Keep polling at the fast interval while the menu is up, even if the pointer has
            // wandered off the bar. Otherwise the tick drops to 100 ms and the colour outlives the
            // menu by up to a tenth of a second, which is long enough to look like lag.
            _proximity.KeepFast = StartActive;
        };

        OnClockTick(null, EventArgs.Empty);
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += OnClockTick;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
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

    /// <summary>Dormant icon extent in DIP; 0 means "whatever the metrics were built with".</summary>
    private double _dormantIconExtent;

    // Drag-reorder state. Null slot means no press is being tracked.
    private TaskbarSlot? _pressedSlot;
    private FrameworkElement? _pressedElement;
    private Point _pressedAt;
    private bool _dragging;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _proximity.Start();
        _clock.Start();
        SuppressActivation();

        // Built here rather than in the constructor because the accumulator is sized in physical
        // pixels and the window has no size until it has been laid out.
        double scale = DpiScale <= 0 ? 1.0 : DpiScale;
        int width = Math.Max(1, (int)Math.Round(ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Round(ActualHeight * scale));

        _wear = new WearSampler(
            width,
            height,
            () => SceneReader.Read(ContentRoot, this),
            () => DpiScale <= 0 ? 1.0 : DpiScale);

        _wear.Start();

        BringUpLog.Write($"wear accumulator: {width}x{height} physical px ({_wear.Accumulator.SizeInBytes / 1024} KB)");

        if (_notificationArea is not null)
        {
            LogTrayContents("at load");
            _notificationArea.TrayIcons.CollectionChanged += (_, _) => LogTrayContents("changed");
        }

        if (MotionEnabled)
        {
            _motion = new MotionDriver(() => CurrentMetrics);
            double trayRatio = Metrics.TrayDriftRatio;

            _motion.OffsetChanged += (dx, dy) =>
            {
                ContentOffset.X = dx;
                ContentOffset.Y = dy;

                TrayOffset.X = dx * trayRatio;
                TrayOffset.Y = dy * trayRatio;
            };

            _motion.ClockOffsetChanged += (dx, dy) =>
            {
                ClockOffset.X = dx;
                ClockOffset.Y = dy;
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
    /// Opens the settings window, which is now the home for everything that used to be a
    /// command-line flag.
    /// </summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app) return;

        new Settings.SettingsWindow(app.Settings, ApplyLiveSettings, app.Restart).Show();
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
        }).Show();
    }

    private void OnHeatmapClick(object sender, RoutedEventArgs e)
    {
        if (_wear is null) return;

        // Flush first, so what the window shows is what is on disk.
        _wear.Persist();
        new HeatmapWindow(_wear.Accumulator) { Owner = null }.Show();
    }

    /// <summary>
    /// Applies the settings that can change without rebuilding the window.
    /// </summary>
    /// <remarks>
    /// The resting icon size and the motion layer are properties of what is drawn, so they take
    /// effect immediately. The edge, the alignment and the monitor are not here: they are fixed by
    /// the AppBar registration and need a restart, which <see cref="Settings.SettingsWindow"/> says
    /// plainly rather than appearing to apply them.
    /// </remarks>
    public void ApplyLiveSettings(Settings.UmbraSettings settings)
    {
        _dormantIconExtent = Math.Clamp(
            settings.DormantIconSize * Metrics.Scale, 1.0, SlotMetrics.BaseIconExtent * Metrics.Scale);

        OnPropertyChanged(nameof(IconExtent));
        _motion?.SetEnabled(settings.Motion);
        _motion?.Reemit();

        BringUpLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"settings applied live: dormant icon {_dormantIconExtent:F0} px, motion {settings.Motion}"));
    }

    /// <summary>Geometry for this bar, straight from the spec model.</summary>
    public BarMetrics Metrics { get; }

    /// <summary>The live window list, as ManagedShell reports it.</summary>
    public ICollectionView Windows { get; }

    /// <summary>Pinned applications (§9 / build order step 9).</summary>
    public PinSettings Pins { get; } = new();

    /// <summary>What the bar actually shows: one slot per application.</summary>
    public ObservableCollection<TaskbarSlot> Slots => _tasks.Slots;

    /// <summary>The notification area's icons (§11.1), or null when tray hosting is off.</summary>
    public ICollectionView? TrayIcons { get; }

    /// <summary>
    /// The notification area's drift offset — the same phase as the icon row, scaled down to what
    /// a tray slot can contain (§6.2, P5).
    /// </summary>
    public TranslateTransform TrayOffset { get; } = new();

    /// <summary>Tray slot size along the bar's long axis.</summary>
    public double TraySlotWidth => IsVertical ? Metrics.ContentBand : Metrics.TraySlot.Pitch;

    /// <inheritdoc cref="TraySlotWidth"/>
    public double TraySlotHeight => IsVertical ? Metrics.TraySlot.Pitch : Metrics.ContentBand;

    /// <summary>Drawn extent of a tray icon.</summary>
    public double TrayIconExtent => Metrics.TraySlot.IconExtent;

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
    public Thickness TrayMargin => IsVertical
        ? new Thickness(0, Metrics.RowInset, 0, 0)
        : new Thickness(0, 0, Metrics.RowInset, 0);

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
            double full = SlotMetrics.BaseIconExtent * Metrics.Scale;

            return IconSizeRamp.Extent(dormant, full, SizeBlend);
        }
    }

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

        // The icon grows with the tier, and its permitted drift amplitude shrinks as it does.
        // Republish the offset now rather than waiting up to half a second for the next motion
        // tick, or the icon would briefly be drawn large while still carrying a dormant-sized
        // offset -- outside its own slot, which is exactly what P5 forbids.
        bar.OnPropertyChanged(nameof(IconExtent));
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
        CoreBarEdge.Left => new Thickness(1, 0, 0, 0),
        CoreBarEdge.Right => new Thickness(0, 0, 1, 0),
        CoreBarEdge.Top => new Thickness(0, 1, 0, 0),
        _ => new Thickness(0, 0, 0, 1),
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

    private void LogTrayContents(string reason)
    {
        if (_notificationArea is null) return;

        // Only the first few, or a chatty tray writes to disk forever.
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
    /// Brings the menu into line with the slot as it opens. Set here rather than bound, because a
    /// ContextMenu lives in its own popup tree and a RelativeSource binding up to the window
    /// resolves to nothing.
    /// </summary>
    private void OnSlotContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSlot slot } element) return;
        if (element.ContextMenu is null) return;

        foreach (object child in element.ContextMenu.Items)
        {
            switch (child)
            {
                case MenuItem { Name: "SlotPinItem" } pin:
                    pin.Header = slot.IsPinned ? "Unpin from bar" : "Pin to bar";
                    pin.IsEnabled = slot.IsPinned || slot.IsRunning;
                    break;

                case MenuItem { Name: "SlotCloseItem" } close:
                    // Closing individual windows belongs to the picker now, so this item is only
                    // meaningful when there is exactly one window to close.
                    close.IsEnabled = slot.Windows.Count == 1;
                    close.Visibility = slot.Windows.Count > 1 ? Visibility.Collapsed : Visibility.Visible;
                    close.Header = "Close window";
                    break;
            }
        }
    }

    /// <summary>
    /// Records a press, so a later move can tell a drag from a click.
    /// </summary>
    /// <remarks>
    /// Only pinned applications reorder. The row is pinned applications in their pinned order
    /// followed by whatever else happens to be running (see <see cref="TaskbarModel"/>), and the
    /// running tail has no stored order to rewrite — dragging within it would appear to work and
    /// be undone by the next rebuild.
    /// </remarks>
    private void OnSlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarSlot slot } element) return;

        if (!slot.IsPinned || Pins.Pins.Count < 2) return;

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
    /// Reordering goes through <see cref="PinSettings"/> rather than moving items in the collection
    /// directly. The model rebuilds itself from the pin order, so a direct collection move would be
    /// reverted the next time any window anywhere opened or closed — visibly, and only sometimes,
    /// which is the worst kind of bug to be handed.
    /// </para>
    /// </remarks>
    private void OnSlotMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedSlot is null || e.LeftButton != MouseButtonState.Pressed) return;

        Point now = e.GetPosition(WindowList);

        if (!_dragging)
        {
            double moved = IsVertical
                ? Math.Abs(now.Y - _pressedAt.Y)
                : Math.Abs(now.X - _pressedAt.X);

            double threshold = IsVertical
                ? SystemParameters.MinimumVerticalDragDistance
                : SystemParameters.MinimumHorizontalDragDistance;

            if (moved < threshold) return;

            _dragging = true;
            _pressedElement?.CaptureMouse();
            SetDragFeedback(_pressedElement, dragging: true);
        }

        // Which slot position the pointer is over, in the undrifted layout (P5: hit geometry does
        // not move, so this needs no drift correction).
        double along = IsVertical ? now.Y : now.X;
        int target = (int)Math.Floor(along / Metrics.Slot.Pitch);
        target = Math.Clamp(target, 0, Pins.Pins.Count - 1);

        Pins.Move(_pressedSlot.Key, target, persist: false);
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
        bool dragged = _dragging;

        _pressedSlot = null;
        _pressedElement = null;
        _dragging = false;

        if (element is not null)
        {
            element.ReleaseMouseCapture();
            SetDragFeedback(element, dragging: false);
        }

        if (!dragged) return;

        Pins.Persist();
        BringUpLog.Write($"reordered pins: {string.Join(", ", Pins.Pins.Select(p => p.Name))}");
    }

    /// <summary>Dims the icon being dragged, so it reads as picked up rather than stuck.</summary>
    private static void SetDragFeedback(FrameworkElement? slotElement, bool dragging)
    {
        if (slotElement is null) return;
        if (FindDescendant<Image>(slotElement) is not { } icon) return;

        icon.Opacity = dragging ? 0.45 : 1.0;
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
            slot.Windows[0].BringToFront();
            return;
        }

        // Already looking at it: minimise, as the Windows taskbar does.
        if (!active.IsMinimized) active.Minimize();
        else active.BringToFront();
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
            _picker.Dismiss();
            _picker = null;

            // A second click on the same application closes the picker rather than reopening it.
            if (sameSlot) return;
        }

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
        string? path = slot.Pin?.Path;

        try
        {
            // A path is a plain executable; anything else is an app user model ID, which is how
            // packaged applications are started and cannot be run as a file.
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                ManagedShell.Common.Helpers.ShellHelper.StartProcess(path, string.Empty, string.Empty, string.Empty);
            else
                ManagedShell.Common.Helpers.ShellHelper.ActivateApplication(slot.Key, string.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            BringUpLog.Write(ex, $"failed to launch {slot.Key}");
        }
    }

    private void OnSlotPinClick(object sender, RoutedEventArgs e)
    {
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
        foreach (ApplicationWindow window in slot.Windows.ToArray()) window.Close();
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

        UpdateTrayHostSize();
        icon.IconMouseDown(e.ChangedButton, PackedCursorPosition(), DoubleClickTime);
        e.Handled = true;
    }

    private void OnTrayIconMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NotifyIcon icon }) return;

        icon.IconMouseUp(e.ChangedButton, PackedCursorPosition(), DoubleClickTime);
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
