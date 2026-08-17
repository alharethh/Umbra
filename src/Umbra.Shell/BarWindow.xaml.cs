using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ManagedShell.AppBar;
using Umbra.Core.Layout;
using Umbra.Shell.Shell;
using Umbra.Shell.Tiers;
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

        DataContext = this;
        InitializeComponent();

        _proximity = new ProximityDriver(
            () => new System.Windows.Rect(Left, Top, Width, Height),
            () => DpiScale <= 0 ? 1.0 : DpiScale);

        _proximity.LevelChanged += level => TierLevel = level;
        Loaded += (_, _) => _proximity.Start();
        Closed += (_, _) => _proximity.Dispose();
    }

    private readonly ProximityDriver _proximity;

    /// <summary>Geometry for this bar, straight from the spec model.</summary>
    public BarMetrics Metrics { get; }

    /// <summary>The live window list.</summary>
    public ICollectionView Windows { get; }

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
    public Thickness RowMargin => Metrics.Alignment == Alignment.Center
        ? default
        : IsVertical
            ? new Thickness(0, 0, 0, Metrics.RowInset)
            : new Thickness(Metrics.RowInset, 0, 0, 0);

    /// <summary>
    /// The continuous tier level, 0..2, driven by pointer proximity (§3.4). A dependency property
    /// so that every tier-dependent binding re-evaluates as the ramp moves.
    /// </summary>
    public static readonly DependencyProperty TierLevelProperty = DependencyProperty.Register(
        nameof(TierLevel), typeof(double), typeof(BarWindow), new PropertyMetadata(0.0));

    /// <inheritdoc cref="TierLevelProperty"/>
    public double TierLevel
    {
        get => (double)GetValue(TierLevelProperty);
        set => SetValue(TierLevelProperty, value);
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

    private static AppBarEdge ToAppBarEdge(CoreBarEdge edge) => edge switch
    {
        CoreBarEdge.Left => AppBarEdge.Left,
        CoreBarEdge.Top => AppBarEdge.Top,
        CoreBarEdge.Right => AppBarEdge.Right,
        CoreBarEdge.Bottom => AppBarEdge.Bottom,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };
}
