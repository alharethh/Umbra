using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.AppBar;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using Umbra.Core.Layout;
using Umbra.Core.Motion;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Motion;
using Umbra.Shell.Rendering;
using Umbra.Shell.Shell;
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

    public BarWindow(ShellHost host, BarMetrics metrics)
        : base(
            host.ShellManager.AppBarManager,
            host.ShellManager.ExplorerHelper,
            host.ShellManager.FullScreenHelper,
            AppBarScreen.FromPrimaryScreen(),
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

        // Null when tray hosting is switched off, which is the default — see ShellHost. Pinned
        // rather than all: the unpinned ones belong behind an overflow chevron, which is §7.1's
        // "tray overflow flyout" and is not built yet.
        _notificationArea = host.ShellManager.NotificationArea;
        TrayIcons = _notificationArea?.PinnedIcons;

        DataContext = this;
        InitializeComponent();

        Outlines.Changed += () => OutlineRevision++;

        // The converter lives in XAML resources, so its dependency is handed to it here.
        if (Resources["TierIcon"] is TierIconConverter converter) converter.Settings = Outlines;

        _proximity = new ProximityDriver(
            () => new System.Windows.Rect(Left, Top, Width, Height),
            () => DpiScale <= 0 ? 1.0 : DpiScale);

        _proximity.LevelChanged += level => TierLevel = level;

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
        };
    }

    private readonly ProximityDriver _proximity;
    private readonly NotificationArea? _notificationArea;
    private readonly DispatcherTimer _clock;
    private MotionDriver? _motion;
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
            _motion = new MotionDriver(Metrics);
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
            BringUpLog.Write("motion disabled by --no-motion");
        }

        BringUpLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"motion: drift +/-{Metrics.Slot.DriftAmplitude:F2} px long axis, " +
            $"+/-{Metrics.ShortAxisDriftAmplitude:F2} px short axis, " +
            $"micro +/-{Metrics.Slot.MicroAmplitude:F2} px at {MotionModel.MicroUpdateRateHz} Hz, " +
            $"peak {Metrics.Slot.PeakOffset:F2} px against a {Metrics.Slot.Gutter:F2} px gutter"));
    }

    private void OnHeatmapClick(object sender, RoutedEventArgs e)
    {
        if (_wear is null) return;

        // Flush first, so what the window shows is what is on disk.
        _wear.Persist();
        new HeatmapWindow(_wear.Accumulator) { Owner = null }.Show();
    }

    /// <summary>Geometry for this bar, straight from the spec model.</summary>
    public BarMetrics Metrics { get; }

    /// <summary>The live window list.</summary>
    public ICollectionView Windows { get; }

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
    /// Seconds are never displayed (§6.4): high-frequency change in a tiny fixed area with almost
    /// no informational value, and the colon does not blink either — halving its duty would double
    /// the transition count for visual noise.
    /// </summary>
    public bool ShowsSeconds => false;

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

    /// <summary>Drawn icon extent. 24 DIP.</summary>
    public double IconExtent => Metrics.Slot.IconExtent;

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
        // The clock's date line appears from Tier 1, and it is bound through a plain property
        // rather than the dependency property, so it needs telling.
        if (d is BarWindow bar) bar.OnPropertyChanged(nameof(ShowsDate));
    }

    /// <inheritdoc cref="TierLevelProperty"/>
    public double TierLevel
    {
        get => (double)GetValue(TierLevelProperty);
        set => SetValue(TierLevelProperty, value);
    }

    /// <summary>
    /// Bumped whenever any application's outline setting changes.
    /// </summary>
    /// <remarks>
    /// The icon bindings include this, so changing one application's setting re-evaluates every
    /// icon. Cheaper than making each icon observe the settings object individually, and the row is
    /// a handful of items.
    /// </remarks>
    public static readonly DependencyProperty OutlineRevisionProperty = DependencyProperty.Register(
        nameof(OutlineRevision), typeof(int), typeof(BarWindow), new PropertyMetadata(0));

    /// <inheritdoc cref="OutlineRevisionProperty"/>
    public int OutlineRevision
    {
        get => (int)GetValue(OutlineRevisionProperty);
        set => SetValue(OutlineRevisionProperty, value);
    }

    /// <summary>Turns outline mode on for every icon at startup — a testing convenience.</summary>
    /// <remarks>
    /// Writes straight through to <see cref="Outlines"/> rather than being stored and copied in the
    /// constructor. Object initializers run <i>after</i> the constructor body, so a constructor that
    /// read this property would always see <see langword="false"/> and the flag would silently do
    /// nothing.
    /// </remarks>
    public bool OutlineAll
    {
        get => Outlines.ApplyToAll;
        init => Outlines.ApplyToAll = value;
    }

    /// <summary>Per-application outline choices (§5.3).</summary>
    public OutlineSettings Outlines { get; } = new();

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
        ManagedShell.Common.Helpers.ShellHelper.ShowStartMenu();
        e.Handled = true;
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
        string time = now.ToString("HH:mm", CultureInfo.CurrentCulture);
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

    /// <summary>Per-icon outline toggle, from that icon's own context menu (§5.3).</summary>
    private void OnSlotOutlineClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ApplicationWindow window })
            Outlines.Toggle(window);
    }

    /// <summary>
    /// Ticks the menu item to match the application's current setting as the menu opens. Set here
    /// rather than bound, because a ContextMenu lives in its own popup tree and a
    /// RelativeSource binding up to the window resolves to nothing.
    /// </summary>
    private void OnSlotContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ApplicationWindow window } element) return;
        if (element.ContextMenu is null) return;

        foreach (object child in element.ContextMenu.Items)
            if (child is MenuItem { Name: "SlotOutlineItem" } item)
                item.IsChecked = Outlines.IsEnabled(window);
    }

    /// <summary>
    /// Click a slot to activate its window, or to minimise it if it is already in front — the
    /// behaviour the Windows taskbar has.
    /// </summary>
    private void OnSlotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ApplicationWindow window }) return;

        if (window.State == ApplicationWindow.WindowState.Active && !window.IsMinimized)
            window.Minimize();
        else
            window.BringToFront();

        e.Handled = true;
    }

    private void OnSlotClose(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ApplicationWindow window })
            window.Close();
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
