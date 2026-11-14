using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ManagedShell.WindowsTray;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Rendering;
using Umbra.Shell.Shell;

namespace Umbra.Shell;

/// <summary>
/// Which notification icons sit on the bar and which behind the chevron, and moving them between
/// the two: by dragging, from the chevron's menu, and from the settings window. See
/// <see cref="TrayChoice"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three rows out of one collection.</b> ManagedShell feeds every icon into one
/// <c>TrayIcons</c> collection; the bar filters it three ways. The system indicators go in the
/// system cluster, whose click opens Quick Settings. The icons the user chose go in a row of their
/// own beside it, where a click reaches the application like any icon in the flyout. Everything
/// else goes behind the chevron.
/// </para>
/// <para>
/// <b>ManagedShell's own pinning is not used for this.</b> Its <c>PinnedIcons</c> view would hold
/// the indicators and the chosen icons together, and the bar would have to split them again
/// anyway; filtering on <see cref="TrayChoice"/> directly leaves one list deciding everything.
/// ManagedShell's pinned list stays the four system indicators, which it needs non-null.
/// </para>
/// </remarks>
internal partial class BarWindow
{
    private TrayChoice? _trayChoice;

    /// <summary>The icons the user has put on the bar, in their order. Null without tray hosting.</summary>
    public ICollectionView? BarTrayIcons { get; private set; }

    /// <summary>
    /// Builds the three views over ManagedShell's icon collection.
    /// </summary>
    /// <remarks>
    /// Live filtering on <c>IsHidden</c>, as ManagedShell's own views do: an application can hide
    /// its icon without removing it. Which view an icon is in otherwise changes only when the
    /// choice does, and <see cref="OnTrayChoiceChanged"/> refreshes the views then.
    /// </remarks>
    private void BuildTrayViews(NotificationArea tray, TrayChoice? choice)
    {
        _trayChoice = choice;

        TrayIcons = View(tray, icon => IsSystemIndicator(icon));
        TrayIcons.SortDescriptions.Add(new SortDescription(nameof(NotifyIcon.PinOrder), ListSortDirection.Ascending));

        ListCollectionView onBar = View(tray, icon => !IsSystemIndicator(icon) && ShowsOnBar(icon));
        onBar.CustomSort = new ChoiceOrder(this, tray.TrayIcons);
        BarTrayIcons = onBar;

        OverflowTrayIcons = View(tray, icon => !IsSystemIndicator(icon) && !ShowsOnBar(icon));

        BarTrayIcons.CollectionChanged += (_, _) => OnTrayIconsChanged();
        OverflowTrayIcons.CollectionChanged += (_, _) => OnTrayIconsChanged();

        if (choice is not null)
        {
            choice.Changed += OnTrayChoiceChanged;
            Closed += (_, _) => choice.Changed -= OnTrayChoiceChanged;
        }
    }

    private static ListCollectionView View(NotificationArea tray, Predicate<NotifyIcon> keep)
    {
        ListCollectionView view = new(tray.TrayIcons)
        {
            Filter = item => item is NotifyIcon { IsHidden: false } icon && keep(icon),
        };

        if (view is ICollectionViewLiveShaping live)
        {
            live.IsLiveFiltering = true;
            live.LiveFilteringProperties.Add(nameof(NotifyIcon.IsHidden));
        }

        return view;
    }

    /// <summary>Volume, network, power and the action centre: always on the bar, in the cluster.</summary>
    internal static bool IsSystemIndicator(NotifyIcon icon) =>
        icon.GUID != Guid.Empty
        && ShellHost.SystemIndicators.Any(id => Guid.TryParse(id, out Guid guid) && guid == icon.GUID);

    private bool ShowsOnBar(NotifyIcon icon) => _trayChoice?.ShowsOnBar(icon.IsEqualByIdentifier) == true;

    /// <summary>
    /// Chosen icons in the user's order, then (with "show all") the rest in arrival order.
    /// </summary>
    private sealed class ChoiceOrder(BarWindow bar, IList source) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not NotifyIcon a || y is not NotifyIcon b) return 0;

            int byChoice = Position(a).CompareTo(Position(b));
            return byChoice != 0 ? byChoice : source.IndexOf(a).CompareTo(source.IndexOf(b));
        }

        private int Position(NotifyIcon icon)
        {
            int position = bar._trayChoice?.PositionOf(icon.IsEqualByIdentifier) ?? -1;
            return position < 0 ? int.MaxValue : position;
        }
    }

    private void OnTrayChoiceChanged()
    {
        BarTrayIcons?.Refresh();
        OverflowTrayIcons?.Refresh();
        OnTrayIconsChanged();
    }

    /// <summary>
    /// The chevron stays while every icon is on the bar, empty as its panel is: it is where the
    /// user turns that off again.
    /// </summary>
    private bool KeepsChevronForShowAll => _trayChoice?.ShowAll == true;

    // ---------------------------------------------------------------- the chevron's menu

    /// <summary>Right-clicking the chevron: the one switch between all on the bar and the menu.</summary>
    private void OnTrayChevronRightClick(object sender, MouseButtonEventArgs e)
    {
        // Handled, or the bar's own context menu opens over this one.
        e.Handled = true;
        OpenTrayChevronMenu();
    }

    private void OpenTrayChevronMenu()
    {
        if (_trayChoice is not { } choice) return;

        ClearPanelsFor(BarPanel.None);
        DismissWindowsSurfaces();

        ContextMenu menu = new()
        {
            Style = MenuStyle("DarkContextMenu"),
            PlacementTarget = TrayChevron,
            Placement = PlacementMode.MousePoint,
        };

        bool showAll = choice.ShowAll;
        AddItem(
            menu,
            TrayChevronMenu.ToggleLabel(showAll),
            icon: null,
            (_, _) => choice.SetShowAll(!showAll));

        menu.IsOpen = true;
        BringUpLog.Write($"tray chevron menu opened, show all = {showAll}");
    }

    // ---------------------------------------------------------------- dragging

    /// <summary>A left press on a tray icon that may yet turn into a drag.</summary>
    private sealed record TrayPress(NotifyIcon Icon, FrameworkElement Element, Point StartScreen, bool FromFlyout);

    private TrayPress? _trayPress;

    /// <summary>The press, once the pointer has moved far enough to be a drag.</summary>
    private TrayPress? _trayDrag;

    private Popup? _trayGhost;
    private Popup? _trayCaret;

    /// <summary>Called from <see cref="OnTrayIconMouseDown"/> for a left press.</summary>
    /// <remarks>The system indicators are not dragged: they live in the system cluster.</remarks>
    private void NoteTrayPress(NotifyIcon icon, FrameworkElement element)
    {
        if (_trayChoice is null || IsSystemIndicator(icon)) return;

        bool fromFlyout = TrayOverflowPopup.IsOpen
            && TrayOverflowPopup.Child is Visual flyout
            && flyout.IsAncestorOf(element);

        _trayPress = new TrayPress(icon, element, CursorScreen(), fromFlyout);
    }

    private void OnTrayIconMouseMove(object sender, MouseEventArgs e)
    {
        if (_trayDrag is { } drag)
        {
            UpdateTrayDrag(drag);
            return;
        }

        if (_trayPress is not { } press || !ReferenceEquals(press.Element, sender)) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _trayPress = null;
            return;
        }

        double dpi = DpiScale <= 0 ? 1.0 : DpiScale;
        if (!TrayDrop.IsDrag(press.StartScreen, CursorScreen(),
                SystemParameters.MinimumHorizontalDragDistance * dpi,
                SystemParameters.MinimumVerticalDragDistance * dpi))
            return;

        BeginTrayDrag(press);
    }

    private void BeginTrayDrag(TrayPress press)
    {
        _trayDrag = press;
        _trayPress = null;

        // Mouse capture rather than OLE drag and drop: the drag never leaves the process, and
        // capture follows the pointer from the flyout's popup window into the bar's.
        press.Element.CaptureMouse();
        press.Element.LostMouseCapture += OnTrayDragLostCapture;

        // Lifted out: what stays in the slot is a faint placeholder.
        Fade(press.Element, TrayDragPlaceholderOpacity, TrayAnimationShort);

        _trayGhost = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.AbsolutePoint,
            Child = new Image
            {
                Source = press.Icon.Icon,
                Width = TrayIconExtent,
                Height = TrayIconExtent,
                Opacity = 0.85,
            },
        };

        _trayCaret = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.AbsolutePoint,
            Child = new Border
            {
                Background = (Brush)FindResource("Umbra.Text"),
                CornerRadius = new CornerRadius(1),
            },
        };

        // The press already reached the application, and some open their menu on it (Bluetooth).
        // A drag is not a click, so that menu goes: here, on every move and on the drop.
        BringUpLog.Write($"tray drag: started on '{press.Icon.Title}' from the {(press.FromFlyout ? "flyout" : "bar")}");
        UpdateTrayDrag(press);
    }

    private void UpdateTrayDrag(TrayPress drag)
    {
        if (_trayGhost is null || _trayCaret is null) return;

        // The press's menu can open a beat after the drag began; it goes as soon as it does.
        TrayMenuWatcher.CancelMenuOf(drag.Icon.HWnd);

        double dpi = DpiScale <= 0 ? 1.0 : DpiScale;
        Point cursor = CursorScreen();

        // AbsolutePoint offsets are device-independent; the cursor is physical. The ghost rides a
        // little inboard of the pointer rather than under it, so the chevron or the gap the
        // pointer is over stays visible.
        Vector lift = TrayDrop.GhostLift(Metrics.Edge, TrayIconExtent);
        _trayGhost.HorizontalOffset = cursor.X / dpi - TrayIconExtent / 2 + lift.X;
        _trayGhost.VerticalOffset = cursor.Y / dpi - TrayIconExtent / 2 + lift.Y;
        _trayGhost.IsOpen = true;

        TrayDropTarget target = TrayDropAt(cursor);

        SetChevronDropLit(target.Kind == TrayDrop.Kind.Overflow);

        if (target.Kind == TrayDrop.Kind.Bar && target.Caret is { } caret)
        {
            double thickness = 2;
            double length = TrayIconExtent;

            Border line = (Border)_trayCaret.Child;
            line.Width = IsVertical ? length : thickness;
            line.Height = IsVertical ? thickness : length;

            _trayCaret.HorizontalOffset = caret.X / dpi - line.Width / 2;
            _trayCaret.VerticalOffset = caret.Y / dpi - line.Height / 2;
            _trayCaret.IsOpen = true;
        }
        else
        {
            _trayCaret.IsOpen = false;
        }
    }

    /// <summary>
    /// Ends a drag. State is cleared before the capture is released, because releasing raises
    /// <c>LostMouseCapture</c> synchronously and would run this again (the same trap the icon
    /// row's <c>EndDrag</c> fell into).
    /// </summary>
    /// <returns>True when a drag was in progress, so the button-up was ours and not the icon's.</returns>
    private bool EndTrayDrag(bool drop)
    {
        _trayPress = null;
        if (_trayDrag is not { } drag) return false;

        _trayDrag = null;
        TrayDropTarget target = drop ? TrayDropAt(CursorScreen()) : TrayDropTarget.None;

        SetChevronDropLit(false);

        bool intoChevron = target.Kind == TrayDrop.Kind.Overflow && !TrayOverflowPopup.IsOpen;
        if (_trayGhost is { } ghost)
        {
            // Into a closed chevron the icon is seen going in; anywhere else it simply goes, and
            // the icon itself arrives in its new place (OnTrayIconLoaded).
            if (intoChevron) FlyGhostInto(ghost, TrayChevron);
            else FadeOutGhost(ghost);
        }

        if (_trayCaret is not null) _trayCaret.IsOpen = false;
        _trayGhost = null;
        _trayCaret = null;

        drag.Element.LostMouseCapture -= OnTrayDragLostCapture;
        if (drag.Element.IsMouseCaptured) drag.Element.ReleaseMouseCapture();

        // Back to full if it stayed where it was. If it moved, this element is about to be
        // replaced and the arrival animation shows it in the new place.
        Fade(drag.Element, 1, TrayAnimationShort);

        TrayMenuWatcher.CancelMenuOf(drag.Icon.HWnd);
        _trayArriving = target.Kind == TrayDrop.Kind.None || intoChevron ? null : drag.Icon;
        ApplyTrayDrop(drag.Icon, target);
        return true;
    }

    // ---------------------------------------------------------------- drag animations
    //
    // Short and plain: a fade, a scale, one small overshoot on arrival. Every one of them is
    // skipped when Windows' animation effects are off. Easing is fine here for the reason the
    // icon row's slide gives: these are one-off transitions, not drift, so they put no residence
    // time anywhere (§6.1 is about turnarounds that repeat for hours).

    private static readonly TimeSpan TrayAnimationShort = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TrayAnimationArrive = TimeSpan.FromMilliseconds(220);
    private const double TrayDragPlaceholderOpacity = 0.3;

    /// <summary>The icon just dropped, so its new container can animate in when it is built.</summary>
    private NotifyIcon? _trayArriving;

    private static Duration Animated(TimeSpan time) =>
        new(SystemParameters.ClientAreaAnimation ? time : TimeSpan.Zero);

    private static void Fade(UIElement element, double to, TimeSpan time)
    {
        DoubleAnimation fade = new(to, Animated(time)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => element.Opacity = to;
        element.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// A tray icon's container has been built. If it is the icon just dropped, it arrives: a
    /// quick scale up from 60% with a touch of overshoot, fading in.
    /// </summary>
    /// <remarks>
    /// Containers are rebuilt when the views refresh after a change, so the arriving icon's
    /// container is new and this is where it first exists. The scale sits on the container, not
    /// on the image, whose transform is the drift offset.
    /// </remarks>
    private void OnTrayIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NotifyIcon icon } element) return;
        if (_trayArriving is null || !ReferenceEquals(icon, _trayArriving)) return;

        _trayArriving = null;
        if (!SystemParameters.ClientAreaAnimation) return;

        ScaleTransform scale = new(0.6, 0.6);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;

        DoubleAnimation grow = new(0.6, 1, Animated(TrayAnimationArrive))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        element.Opacity = 0;
        Fade(element, 1, TrayAnimationShort);
    }

    /// <summary>The floating icon shrinks into the chevron and is gone; the chevron bounces as it lands.</summary>
    private void FlyGhostInto(Popup ghost, FrameworkElement target)
    {
        if (!SystemParameters.ClientAreaAnimation || ghost.Child is not FrameworkElement image)
        {
            ghost.IsOpen = false;
            PlayPress(TrayChevronScale);
            return;
        }

        double dpi = DpiScale <= 0 ? 1.0 : DpiScale;
        Rect into = ScreenRect(target);
        double toX = (into.Left + into.Width / 2) / dpi - TrayIconExtent / 2;
        double toY = (into.Top + into.Height / 2) / dpi - TrayIconExtent / 2;

        Duration time = Animated(TrayAnimationShort);
        QuadraticEase ease = new() { EasingMode = EasingMode.EaseIn };

        ScaleTransform shrink = new(1, 1);
        image.RenderTransformOrigin = new Point(0.5, 0.5);
        image.RenderTransform = shrink;

        // The move is set frame by frame. Animating a popup's offsets does not move its window
        // (measured: the icon shrank where it stood); setting them does, which is how the drag
        // itself moves it.
        double fromX = ghost.HorizontalOffset;
        double fromY = ghost.VerticalOffset;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        void Step(object? sender, EventArgs e)
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / TrayAnimationShort.TotalMilliseconds);
            double eased = ease.Ease(t);
            ghost.HorizontalOffset = fromX + (toX - fromX) * eased;
            ghost.VerticalOffset = fromY + (toY - fromY) * eased;

            if (t < 1) return;

            CompositionTarget.Rendering -= Step;
            ghost.IsOpen = false;
            PlayPress(TrayChevronScale);
        }

        CompositionTarget.Rendering += Step;
        shrink.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, time) { EasingFunction = ease });
        shrink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, time) { EasingFunction = ease });
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time));
    }

    private static void FadeOutGhost(Popup ghost)
    {
        if (!SystemParameters.ClientAreaAnimation || ghost.Child is not UIElement image)
        {
            ghost.IsOpen = false;
            return;
        }

        DoubleAnimation fade = new(0, Animated(TimeSpan.FromMilliseconds(110)));
        fade.Completed += (_, _) => ghost.IsOpen = false;
        image.BeginAnimation(OpacityProperty, fade);
    }

    private bool _chevronDropLit;

    /// <summary>
    /// Lights the chevron while a drop would put the icon back behind it: its hover chip, a little
    /// growth, and the dragged icon fading as if already on its way in.
    /// </summary>
    /// <remarks>
    /// Done from here because the chip's own trigger is the pointer being over the chevron, and
    /// that never fires during a drag: the dragged icon holds the mouse capture. The bar gets its
    /// caret for the same reason; without this the chevron gave no sign it would take the drop.
    /// </remarks>
    private void SetChevronDropLit(bool lit)
    {
        if (_chevronDropLit == lit) return;
        _chevronDropLit = lit;

        TimeSpan time = TimeSpan.FromMilliseconds(lit ? 140 : 200);
        Duration duration = SystemParameters.ClientAreaAnimation ? new Duration(time) : new Duration(TimeSpan.Zero);

        if (lit)
        {
            TrayChevronChip.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration));
            TrayChevronScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.18, duration));
            TrayChevronScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.18, duration));
        }
        else
        {
            // Back to whatever the chip's own style says, then the scale back to rest.
            TrayChevronChip.BeginAnimation(OpacityProperty, null);
            TrayChevronScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration));
            TrayChevronScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration));
        }

        if (_trayGhost?.Child is UIElement ghost) ghost.Opacity = lit ? 0.45 : 0.85;
    }

    private void OnTrayDragLostCapture(object sender, MouseEventArgs e) => EndTrayDrag(drop: false);

    private void ApplyTrayDrop(NotifyIcon icon, TrayDropTarget target)
    {
        if (_trayChoice is not { } choice) return;

        switch (target.Kind)
        {
            case TrayDrop.Kind.Bar:
                int index = choice.IndexBefore(target.Before is { } before ? before.IsEqualByIdentifier : null);
                bool placed = choice.Place(icon.Identifier, icon.IsEqualByIdentifier, index);
                BringUpLog.Write($"tray drag: '{icon.Title}' dropped on the bar at {index}, changed={placed}");
                break;

            case TrayDrop.Kind.Overflow:
                bool removed = choice.Remove(icon.IsEqualByIdentifier);
                BringUpLog.Write($"tray drag: '{icon.Title}' dropped in the menu, changed={removed}");
                break;

            default:
                BringUpLog.Write($"tray drag: '{icon.Title}' dropped nowhere, nothing changed");
                break;
        }
    }

    /// <summary>Where a drop at this point would go.</summary>
    private readonly record struct TrayDropTarget(TrayDrop.Kind Kind, NotifyIcon? Before, Point? Caret)
    {
        public static TrayDropTarget None => new(TrayDrop.Kind.None, null, null);
    }

    /// <summary>
    /// The menu while it is open (or the chevron, which stands for it), else the bar, else nowhere.
    /// All in physical pixels, as <see cref="PointerIsOverTrayOverflow"/> reads them.
    /// </summary>
    private TrayDropTarget TrayDropAt(Point cursor)
    {
        // With every icon on the bar the menu holds nothing, so there is nothing to drop into.
        if (_trayChoice?.ShowAll != true && TrayOverflowPopup.IsOpen && PointerIsOverTrayOverflow())
            return new TrayDropTarget(TrayDrop.Kind.Overflow, null, null);

        if (_trayChoice?.ShowAll != true && TrayChevron.IsVisible && ScreenRect(TrayChevron).Contains(cursor))
            return new TrayDropTarget(TrayDrop.Kind.Overflow, null, null);

        double dpi = DpiScale <= 0 ? 1.0 : DpiScale;
        Rect bar = new(Left * dpi, Top * dpi, ActualWidth * dpi, ActualHeight * dpi);
        if (!bar.Contains(cursor)) return TrayDropTarget.None;

        List<(NotifyIcon Icon, Rect Slot)> slots = [];
        if (BarTrayIcons is not null)
        {
            foreach (NotifyIcon icon in BarTrayIcons.Cast<NotifyIcon>())
            {
                if (TrayBarHost.ItemContainerGenerator.ContainerFromItem(icon) is FrameworkElement { IsVisible: true } container)
                    slots.Add((icon, ScreenRect(container)));
            }
        }

        double along = IsVertical ? cursor.Y : cursor.X;
        int gap = TrayDrop.GapIndex([.. slots.Select(s => IsVertical ? (s.Slot.Top, s.Slot.Bottom) : (s.Slot.Left, s.Slot.Right))], along);

        Point caretAt;
        if (slots.Count == 0)
        {
            // An empty row has no size; its place is the start of the system cluster.
            Rect cluster = ScreenRect(SystemCluster);
            caretAt = IsVertical ? new Point(cluster.Left + cluster.Width / 2, cluster.Top)
                                 : new Point(cluster.Left, cluster.Top + cluster.Height / 2);
        }
        else
        {
            Rect slot = gap < slots.Count ? slots[gap].Slot : slots[^1].Slot;
            bool leading = gap < slots.Count;
            caretAt = IsVertical
                ? new Point(slot.Left + slot.Width / 2, leading ? slot.Top : slot.Bottom)
                : new Point(leading ? slot.Left : slot.Right, slot.Top + slot.Height / 2);
        }

        NotifyIcon? before = gap < slots.Count ? slots[gap].Icon : null;
        return new TrayDropTarget(TrayDrop.Kind.Bar, before, caretAt);
    }

    private static Rect ScreenRect(FrameworkElement element)
    {
        Point origin = element.PointToScreen(new Point(0, 0));
        Point far = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(origin, far);
    }

    private static Point CursorScreen() =>
        TrayNative.GetCursorPos(out TrayNative.POINT cursor) ? new Point(cursor.X, cursor.Y) : default;

    // ---------------------------------------------------------------- the settings list

    /// <summary>
    /// What the settings window lists: every icon it could put on the bar, running or chosen.
    /// </summary>
    internal IReadOnlyList<TrayIconRow> TrayIconRows()
    {
        if (_notificationArea is null || _trayChoice is null) return [];

        List<TrayIconRow.Running> running = [];
        foreach (NotifyIcon icon in _notificationArea.TrayIcons.ToArray())
        {
            if (icon.IsHidden || IsSystemIndicator(icon)) continue;

            running.Add(new TrayIconRow.Running(
                icon.Identifier,
                TrayIconName.For(icon.Title, icon.GUID, icon.Path, TrayIconName.Describe),
                icon.IsEqualByIdentifier,
                icon.Icon));
        }

        return TrayIconRow.For(_trayChoice, running);
    }
}
