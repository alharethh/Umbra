using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
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

        int onBar = BarTrayIcons?.Cast<object>().Count() ?? 0;
        int inMenu = OverflowTrayIcons?.Cast<object>().Count() ?? 0;
        IReadOnlyList<TrayChevronMenu.Item> offers = TrayChevronMenu.Offers(choice.ShowAll, onBar, inMenu);

        BringUpLog.Write($"tray chevron menu: show all = {choice.ShowAll}, {onBar} on the bar, {inMenu} in the menu, offers {string.Join(" + ", offers)}");
        if (offers.Count == 0) return;

        foreach (TrayChevronMenu.Item item in offers)
        {
            AddItem(
                menu,
                TrayChevronMenu.LabelOf(item),
                icon: null,
                (_, _) =>
                {
                    if (item == TrayChevronMenu.Item.ShowAll) choice.ShowAllOnBar();
                    else choice.PutAllBack();
                });
        }

        menu.IsOpen = true;
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
        _trayTarget = TrayDropTarget.None;
        _trayChevronShift = 0;

        // Mouse capture rather than OLE drag and drop: the drag never leaves the process, and
        // capture follows the pointer from the flyout's popup window into the bar's.
        press.Element.CaptureMouse();
        press.Element.LostMouseCapture += OnTrayDragLostCapture;

        // Lifted out. From the bar its slot is drawn as the open gap (the marker), so the icon
        // itself goes; from the menu a faint copy stays where it was.
        Fade(press.Element, press.FromFlyout ? TrayDragPlaceholderOpacity : 0, TrayAnimationShort);

        _trayGhost = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.AbsolutePoint,
            Child = GhostCard(press.Icon),
        };

        _trayCaret = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.AbsolutePoint,
            Child = GapMarker(),
        };

        // The press already reached the application, and some open their menu on it (Bluetooth).
        // A drag is not a click, so that menu goes: here, on every move and on the drop.
        BringUpLog.Write($"tray drag: started on '{press.Icon.Title}' from the {(press.FromFlyout ? "flyout" : "bar")}");
        UpdateTrayDrag(press);
    }

    private void UpdateTrayDrag(TrayPress drag)
    {
        if (_trayGhost is null || _trayCaret is null || _trayLanding) return;

        // The press's menu can open a beat after the drag began; it goes as soon as it does.
        TrayMenuWatcher.CancelMenuOf(drag.Icon.HWnd);

        double dpi = DpiScale <= 0 ? 1.0 : DpiScale;
        Point cursor = CursorScreen();

        // Right under the pointer: the row makes room for it, so nothing it is over needs to stay
        // visible beside it. AbsolutePoint offsets are device-independent; the cursor is physical.
        _trayGhost.HorizontalOffset = cursor.X / dpi - GhostSize / 2 + DropAlignmentFix(GhostSize);
        _trayGhost.VerticalOffset = cursor.Y / dpi - GhostSize / 2;
        _trayGhost.IsOpen = true;

        TrayDropTarget target = TrayDropAt(cursor, drag);
        _trayTarget = target;

        SetChevronDropLit(target.Kind == TrayDrop.Kind.Overflow);
        UpdateSpringOpen(target);
        ApplyRoom(drag, target);
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
        TrayDropTarget target = drop ? TrayDropAt(CursorScreen(), drag) : TrayDropTarget.None;

        StopSpringOpen();
        SetChevronDropLit(false);

        drag.Element.LostMouseCapture -= OnTrayDragLostCapture;
        if (drag.Element.IsMouseCaptured) drag.Element.ReleaseMouseCapture();
        TrayMenuWatcher.CancelMenuOf(drag.Icon.HWnd);

        Popup? ghost = _trayGhost;
        Popup? marker = _trayCaret;
        _trayGhost = null;
        _trayCaret = null;

        // The row as it will be drawn while the icon lands: the gap where it goes, or back home.
        RoomShown room = ApplyRoom(drag, target);
        bool changes = WouldChange(drag, target);

        // Where the icon lands, and whether it shrinks into the chevron to get there.
        (Point to, bool shrink, TimeSpan time) = Landing(drag, target, room, changes);

        BringUpLog.Write($"tray drag: '{drag.Icon.Title}' landing {(changes ? "in its new place" : "back home")} ({target.Kind})");

        _trayLanding = true;
        if (ghost is null)
        {
            Landed();
            return true;
        }

        FlyGhost(ghost, to, shrink, time, Landed);
        return true;

        void Landed()
        {
            _trayLanding = false;
            if (shrink) PlayPress(TrayChevronScale);
            SettleTray(drag, ghost, marker, () =>
            {
                if (changes) ApplyTrayDrop(drag.Icon, target);
                else BringUpLog.Write($"tray drag: '{drag.Icon.Title}' dropped where nothing changes");
            });
        }
    }

    // ---------------------------------------------------------------- drag animations
    //
    // Every one of them is skipped when Windows' animation effects are off. Easing is fine here
    // for the reason the icon row's slide gives: these are one-off transitions, not drift, so
    // they put no residence time anywhere (§6.1 is about turnarounds that repeat for hours).
    // Nothing is offset at rest: every slide ends at zero, so P5 holds whenever no drag is on.

    private static readonly TimeSpan TrayAnimationShort = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TraySlide = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan TrayGlide = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TraySnapBack = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan TrayShrinkIn = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan TraySpringDelay = TimeSpan.FromMilliseconds(500);
    private const double TrayDragPlaceholderOpacity = 0.3;

    /// <summary>Where a drop at the pointer would go, as of the last move.</summary>
    private TrayDropTarget _trayTarget = TrayDropTarget.None;

    /// <summary>The slots the chevron is drawn from its own place, as of the last move.</summary>
    private int _trayChevronShift;

    /// <summary>True while the dragged icon is flying to where it lands; presses wait for it.</summary>
    private bool _trayLanding;

    /// <summary>Where each element is being slid to along the bar, so a move does not restart a slide.</summary>
    private readonly Dictionary<UIElement, double> _traySlideTargets = [];

    private static Duration Animated(TimeSpan time) =>
        new(SystemParameters.ClientAreaAnimation ? time : TimeSpan.Zero);

    private static void Fade(UIElement element, double to, TimeSpan time)
    {
        DoubleAnimation fade = new(to, Animated(time)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => element.Opacity = to;
        element.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// What to add to an absolutely placed popup's horizontal offset so its left edge lands on it.
    /// </summary>
    /// <remarks>
    /// With Windows' handedness set to right-handed (Tablet PC settings, "menus appear to the left
    /// of your hand", the default on a touch laptop), WPF opens every popup with its right edge at
    /// the point, AbsolutePoint included. Measured on the 150% laptop: the floating icon sat its
    /// own width left of the pointer and the gap marker a marker's width left of its slot. The old
    /// caret, 2 px wide, hid it.
    /// </remarks>
    private static double DropAlignmentFix(double width) => SystemParameters.MenuDropAlignment ? width : 0;

    /// <summary>The floating icon: lifted on a small card, a touch larger than the icon at rest.</summary>
    private double GhostSize => TrayIconExtent + 12;

    private Border GhostCard(NotifyIcon icon)
    {
        Color accent = AccentColor;
        Border card = new()
        {
            Width = GhostSize,
            Height = GhostSize,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x5A, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Direction = 270, Opacity = 0.65, Color = Colors.Black },
            Child = new Image
            {
                Source = icon.Icon,
                Width = TrayIconExtent,
                Height = TrayIconExtent,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1.12, 1.12),
            },
        };
        return card;
    }

    /// <summary>The open slot: a dashed outline in the bar's capped accent, faintly filled.</summary>
    private Rectangle GapMarker()
    {
        Color accent = AccentColor;
        return new Rectangle
        {
            RadiusX = 5,
            RadiusY = 5,
            StrokeThickness = 1,
            StrokeDashArray = [3, 2],
            Stroke = new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B)),
            Fill = new SolidColorBrush(Color.FromArgb(0x14, accent.R, accent.G, accent.B)),
        };
    }

    /// <summary>The bar's row of chosen icons, measured where layout put them (no slides).</summary>
    private sealed record TrayRow(
        List<NotifyIcon> Icons, List<FrameworkElement> Containers,
        double FirstSlotStart, double Pitch, double PitchDip, double CrossCentre);

    private TrayRow MeasureTrayRow()
    {
        double dpi = DpiScale <= 0 ? 1.0 : DpiScale;
        List<NotifyIcon> icons = [];
        List<FrameworkElement> containers = [];

        if (BarTrayIcons is not null)
        {
            foreach (NotifyIcon icon in BarTrayIcons.Cast<NotifyIcon>())
            {
                if (TrayBarHost.ItemContainerGenerator.ContainerFromItem(icon) is FrameworkElement { IsVisible: true } container)
                {
                    icons.Add(icon);
                    containers.Add(container);
                }
            }
        }

        double pitchDip = TraySlotMetrics.Pitch;
        double first;
        if (containers.Count > 0)
        {
            Rect slot = LayoutScreenRect(containers[0]);
            first = IsVertical ? slot.Top : slot.Left;
        }
        else
        {
            // An empty row has no size, but layout still puts it between the chevron and the
            // cluster; its first slot would start there.
            Rect host = LayoutScreenRect(TrayBarHost);
            first = IsVertical ? host.Top : host.Left;
        }

        double cross = IsVertical ? (Left + ActualWidth / 2) * dpi : (Top + ActualHeight / 2) * dpi;
        return new TrayRow(icons, containers, first, pitchDip * dpi, pitchDip, cross);
    }

    /// <summary>The middle of a slot of the row, in physical pixels; -1 is the slot before the first.</summary>
    private Point SlotCentre(TrayRow row, int slot)
    {
        double along = row.FirstSlotStart + (slot + 0.5) * row.Pitch;
        return IsVertical ? new Point(row.CrossCentre, along) : new Point(along, row.CrossCentre);
    }

    /// <summary>What <see cref="ApplyRoom"/> drew: the row it measured and the room it made.</summary>
    private readonly record struct RoomShown(TrayRow Row, TrayDrop.RoomLayout Room, int? From);

    /// <summary>
    /// Slides the row's icons and the chevron to make room for the drag, and puts the marker on
    /// the open slot. See <see cref="TrayDrop.Room"/> for the rule, and why the chevron holds still.
    /// </summary>
    private RoomShown ApplyRoom(TrayPress drag, TrayDropTarget target)
    {
        TrayRow row = MeasureTrayRow();
        int index = drag.FromFlyout ? -1 : row.Icons.IndexOf(drag.Icon);
        int? from = index >= 0 ? index : null;
        int? gap = target.Kind == TrayDrop.Kind.Bar ? target.Gap : null;

        // An icon from the menu that never reached the row leaves the row alone.
        TrayDrop.RoomLayout room = TrayDrop.Room(row.Icons.Count, from, gap);

        for (int j = 0; j < row.Containers.Count; j++)
        {
            if (j == from) continue;
            SlideTo(row.Containers[j], room.Offsets[j] * row.PitchDip);
        }

        _trayChevronShift = room.ChevronOffset;
        SlideTo(TrayChevron, room.ChevronOffset * row.PitchDip);

        if (_trayCaret?.Child is Rectangle mark && room.GapSlot is int slot)
        {
            double dpi = DpiScale <= 0 ? 1.0 : DpiScale;
            double along = row.PitchDip - 4;
            double across = TrayIconExtent + 8;
            mark.Width = IsVertical ? across : along;
            mark.Height = IsVertical ? along : across;

            Point centre = SlotCentre(row, slot);
            _trayCaret.HorizontalOffset = centre.X / dpi - mark.Width / 2 + DropAlignmentFix(mark.Width);
            _trayCaret.VerticalOffset = centre.Y / dpi - mark.Height / 2;
            _trayCaret.IsOpen = true;
        }
        else if (_trayCaret is not null)
        {
            _trayCaret.IsOpen = false;
        }

        return new RoomShown(row, room, from);
    }

    /// <summary>Slides an element along the bar to an offset, from wherever it is drawn now.</summary>
    private void SlideTo(FrameworkElement element, double along)
    {
        if (_traySlideTargets.TryGetValue(element, out double current) && current == along) return;
        if (along == 0 && !_traySlideTargets.ContainsKey(element)) return;
        _traySlideTargets[element] = along;

        TranslateTransform shift = SlideTransform(element);
        DoubleAnimation slide = new(along, Animated(TraySlide))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        shift.BeginAnimation(IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private static TranslateTransform SlideTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing && !existing.IsFrozen) return existing;

        TranslateTransform shift = new();
        element.RenderTransform = shift;
        return shift;
    }

    /// <summary>Where an element is drawn along the bar right now, slide included, in physical pixels.</summary>
    private double DrawnAlong(FrameworkElement element)
    {
        double dpi = DpiScale <= 0 ? 1.0 : DpiScale;
        Rect laid = LayoutScreenRect(element);
        double slid = element.RenderTransform is TranslateTransform t ? (IsVertical ? t.Y : t.X) : 0;
        return (IsVertical ? laid.Top : laid.Left) + slid * dpi;
    }

    /// <summary>Takes every slide off at once, leaving each element where layout puts it.</summary>
    private void ClearSlides()
    {
        foreach (UIElement element in _traySlideTargets.Keys)
        {
            if (element is FrameworkElement { RenderTransform: TranslateTransform shift })
            {
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                shift.X = 0;
                shift.Y = 0;
            }
        }
        _traySlideTargets.Clear();
    }

    /// <summary>Whether this drop changes anything at all.</summary>
    /// <remarks>Along the bar, the gap is counted among the other icons, so the icon's own place is its own index.</remarks>
    private bool WouldChange(TrayPress drag, TrayDropTarget target) => target.Kind switch
    {
        TrayDrop.Kind.Overflow => !drag.FromFlyout,
        TrayDrop.Kind.Bar when drag.FromFlyout => true,
        TrayDrop.Kind.Bar => target.Gap != MeasureTrayRow().Icons.IndexOf(drag.Icon),
        _ => false,
    };

    /// <summary>Where the dragged icon flies to when it is let go, in physical pixels.</summary>
    private (Point To, bool Shrink, TimeSpan Time) Landing(TrayPress drag, TrayDropTarget target, RoomShown shown, bool changes)
    {
        Rect chevron = LayoutScreenRect(TrayChevron);
        Point intoChevron = new(chevron.Left + chevron.Width / 2, chevron.Top + chevron.Height / 2);

        if (target.Kind == TrayDrop.Kind.Bar && shown.Room.GapSlot is int slot)
            return (SlotCentre(shown.Row, slot), false, TrayGlide);

        if (target.Kind == TrayDrop.Kind.Overflow && changes)
        {
            if (TrayOverflowPopup.IsOpen && FutureOverflowCell(drag.Icon) is { } cell)
            {
                // The menu is centred on the chevron, and once the row has closed up behind the
                // icon the chevron is a slot further in; the cell goes with it.
                if (!drag.FromFlyout) cell += IsVertical ? new Vector(0, shown.Row.Pitch) : new Vector(shown.Row.Pitch, 0);
                return (cell, false, TrayGlide);
            }

            return (intoChevron, true, TrayShrinkIn);
        }

        // Nothing changes: home. From the bar that is its own slot, held open the whole time.
        if (!drag.FromFlyout && shown.Room.GapSlot is int home)
            return (SlotCentre(shown.Row, home), false, TraySnapBack);

        if (drag.FromFlyout && TrayOverflowPopup.IsOpen && drag.Element.IsVisible)
        {
            Rect at = ScreenRect(drag.Element);
            return (new Point(at.Left + at.Width / 2, at.Top + at.Height / 2), false, TraySnapBack);
        }

        return (intoChevron, true, TrayShrinkIn);
    }

    /// <summary>
    /// The menu cell an icon put back will appear in. The menu lists icons in the order they
    /// arrived, so this is not simply the end.
    /// </summary>
    private Point? FutureOverflowCell(NotifyIcon icon)
    {
        if (OverflowTrayIcons is null || _notificationArea is null) return null;

        IList source = _notificationArea.TrayIcons;
        int place = source.IndexOf(icon);
        List<FrameworkElement> cells = [];
        int index = 0;
        foreach (NotifyIcon other in OverflowTrayIcons.Cast<NotifyIcon>())
        {
            if (TrayOverflowHost.ItemContainerGenerator.ContainerFromItem(other) is not FrameworkElement { IsVisible: true } cell) continue;
            if (source.IndexOf(other) < place) index++;
            cells.Add(cell);
        }

        if (cells.Count == 0) return null;

        Rect target;
        if (index < cells.Count)
        {
            target = ScreenRect(cells[index]);
        }
        else
        {
            Rect last = ScreenRect(cells[^1]);
            int columns = Math.Max(1, TrayOverflowColumns);
            target = cells.Count % columns != 0
                ? new Rect(last.Right, last.Top, last.Width, last.Height)
                : new Rect(ScreenRect(cells[cells.Count - columns]).Left, last.Bottom, last.Width, last.Height);
        }

        return new Point(target.Left + target.Width / 2, target.Top + target.Height / 2);
    }

    /// <summary>
    /// Flies the floating icon to a point, then calls <paramref name="landed"/>. Shrinking, it
    /// fades into the chevron as it goes.
    /// </summary>
    private void FlyGhost(Popup ghost, Point toCentre, bool shrink, TimeSpan time, Action landed)
    {
        double dpi = DpiScale <= 0 ? 1.0 : DpiScale;
        double toX = toCentre.X / dpi - GhostSize / 2 + DropAlignmentFix(GhostSize);
        double toY = toCentre.Y / dpi - GhostSize / 2;

        if (!SystemParameters.ClientAreaAnimation || ghost.Child is not FrameworkElement card)
        {
            ghost.HorizontalOffset = toX;
            ghost.VerticalOffset = toY;
            if (shrink) ghost.IsOpen = false;
            landed();
            return;
        }

        IEasingFunction ease = shrink
            ? new QuadraticEase { EasingMode = EasingMode.EaseIn }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        // The move is set frame by frame. Animating a popup's offsets does not move its window
        // (measured: the icon shrank where it stood); setting them does, which is how the drag
        // itself moves it.
        double fromX = ghost.HorizontalOffset;
        double fromY = ghost.VerticalOffset;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        void Step(object? sender, EventArgs e)
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / time.TotalMilliseconds);
            double eased = ease.Ease(t);
            ghost.HorizontalOffset = fromX + (toX - fromX) * eased;
            ghost.VerticalOffset = fromY + (toY - fromY) * eased;

            if (t < 1) return;

            CompositionTarget.Rendering -= Step;
            if (shrink) ghost.IsOpen = false;
            landed();
        }

        CompositionTarget.Rendering += Step;

        if (shrink && card.RenderTransform is ScaleTransform scale)
        {
            Duration duration = Animated(time);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, duration) { EasingFunction = ease });
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration));
        }
    }

    /// <summary>
    /// Commits the drop and lets the row settle: everything that moved slides from where it was
    /// drawn to where layout now puts it, the chevron included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how the row closes up only after an icon has gone into the chevron, and why a drop
    /// never jumps: where each icon and the chevron were drawn is read before the commit, the
    /// slides come off, the commit rebuilds the row, and once it is laid out each element slides
    /// in from its old drawn place. Where the drawing already matched the new layout (a drop on
    /// the bar) nothing moves at all.
    /// </para>
    /// <para>
    /// The floating icon closes in the same pass the new row first draws in, so the dropped icon
    /// is never missing for a frame.
    /// </para>
    /// </remarks>
    private void SettleTray(TrayPress drag, Popup? ghost, Popup? marker, Action commit)
    {
        TrayRow row = MeasureTrayRow();
        Dictionary<NotifyIcon, double> drawn = [];
        for (int j = 0; j < row.Icons.Count; j++)
        {
            if (ReferenceEquals(row.Icons[j], drag.Icon)) continue;
            drawn[row.Icons[j]] = DrawnAlong(row.Containers[j]);
        }
        double chevronDrawn = DrawnAlong(TrayChevron);

        ClearSlides();
        _trayChevronShift = 0;
        _trayTarget = TrayDropTarget.None;

        drag.Element.BeginAnimation(OpacityProperty, null);
        drag.Element.Opacity = 1;

        commit();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (marker is not null) marker.IsOpen = false;
            if (ghost is not null) ghost.IsOpen = false;

            if (!SystemParameters.ClientAreaAnimation) return;
            double dpi = DpiScale <= 0 ? 1.0 : DpiScale;

            TrayRow after = MeasureTrayRow();
            for (int j = 0; j < after.Icons.Count; j++)
            {
                if (drawn.TryGetValue(after.Icons[j], out double was))
                    SlideFrom(after.Containers[j], (was - DrawnAlong(after.Containers[j])) / dpi);
            }

            if (TrayChevron.IsVisible) SlideFrom(TrayChevron, (chevronDrawn - DrawnAlong(TrayChevron)) / dpi);
        });
    }

    /// <summary>Draws an element offset along the bar and slides it home to zero.</summary>
    private void SlideFrom(FrameworkElement element, double offset)
    {
        if (Math.Abs(offset) < 0.5) return;

        TranslateTransform shift = SlideTransform(element);
        DoubleAnimation slide = new(offset, 0, Animated(TraySlide))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        shift.BeginAnimation(IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty, slide);
    }

    // ---------------------------------------------------------------- the chevron opens on hover

    private DispatcherTimer? _traySpringTimer;

    /// <summary>
    /// Holding a dragged icon over the closed chevron opens its menu after half a second, so it
    /// can be dropped among the hidden icons; a ring fills around the chevron meanwhile.
    /// </summary>
    private void UpdateSpringOpen(TrayDropTarget target)
    {
        bool arm = target.Kind == TrayDrop.Kind.Overflow
            && !TrayOverflowPopup.IsOpen
            && OverflowTrayIcons?.Cast<object>().Any() == true;

        if (!arm)
        {
            StopSpringOpen();
            return;
        }

        if (_traySpringTimer?.IsEnabled == true) return;

        _traySpringTimer ??= new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TraySpringDelay };
        _traySpringTimer.Tick -= OnSpringOpen;
        _traySpringTimer.Tick += OnSpringOpen;
        _traySpringTimer.Start();
        ShowSpringRing(true);
    }

    private void OnSpringOpen(object? sender, EventArgs e)
    {
        StopSpringOpen();
        if (_trayDrag is null || TrayOverflowPopup.IsOpen) return;

        ClearPanelsFor(BarPanel.TrayOverflow);
        DismissWindowsSurfaces();
        SetTrayOverflowOpen(true);

        // A popup opened later draws over one opened earlier, so the menu now covers the floating
        // icon, which would fly into it unseen. Reopening puts it back on top.
        foreach (Popup? above in new[] { _trayCaret, _trayGhost })
        {
            if (above is not { IsOpen: true }) continue;
            above.IsOpen = false;
            above.IsOpen = true;
        }

        BringUpLog.Write("tray drag: held over the chevron, menu opened");
    }

    private void StopSpringOpen()
    {
        _traySpringTimer?.Stop();
        ShowSpringRing(false);
    }

    private void ShowSpringRing(bool show)
    {
        if (!show)
        {
            TrayChevronRing.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            TrayChevronRing.Visibility = Visibility.Collapsed;
            return;
        }

        // Dash lengths are in multiples of the stroke's thickness.
        double circumference = Math.PI * (TrayChevronRing.Width - TrayChevronRing.StrokeThickness) / TrayChevronRing.StrokeThickness;
        TrayChevronRing.Stroke = new SolidColorBrush(AccentColor);
        TrayChevronRing.StrokeDashArray = [circumference, circumference];
        TrayChevronRing.Visibility = Visibility.Visible;

        if (!SystemParameters.ClientAreaAnimation)
        {
            TrayChevronRing.StrokeDashOffset = 0;
            return;
        }

        TrayChevronRing.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(circumference, 0, new Duration(TraySpringDelay)));
    }

    private bool _chevronDropLit;

    /// <summary>
    /// Lights the chevron while a drop would put the icon back behind it: its hover chip, a little
    /// growth, and the dragged icon fading as if already on its way in.
    /// </summary>
    /// <remarks>
    /// Done from here because the chip's own trigger is the pointer being over the chevron, and
    /// that never fires during a drag: the dragged icon holds the mouse capture.
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

        if (_trayGhost?.Child is UIElement ghost) ghost.Opacity = lit ? 0.7 : 1;
    }

    private void OnTrayDragLostCapture(object sender, MouseEventArgs e) => EndTrayDrag(drop: false);

    private void ApplyTrayDrop(NotifyIcon icon, TrayDropTarget target)
    {
        if (_trayChoice is not { } choice) return;

        switch (target.Kind)
        {
            case TrayDrop.Kind.Bar:
                bool placed = choice.PlaceOnBar(
                    icon.Identifier,
                    icon.IsEqualByIdentifier,
                    target.Before is { } before ? before.IsEqualByIdentifier : null,
                    DrawnTrayRow());
                BringUpLog.Write($"tray drag: '{icon.Title}' dropped on the bar before '{target.Before?.Title}', changed={placed}");
                break;

            case TrayDrop.Kind.Overflow:
                bool removed = choice.PutBehindChevron(icon.IsEqualByIdentifier, DrawnTrayRow());
                BringUpLog.Write($"tray drag: '{icon.Title}' dropped in the menu, changed={removed}");
                break;

            default:
                BringUpLog.Write($"tray drag: '{icon.Title}' dropped nowhere, nothing changed");
                break;
        }
    }

    /// <summary>The row as the bar draws it, system indicators left out.</summary>
    internal List<TrayEntry> DrawnTrayRow() =>
        BarTrayIcons is null
            ? []
            : [.. BarTrayIcons.Cast<NotifyIcon>().Select(i => new TrayEntry(i.Identifier, i.IsEqualByIdentifier))];

    /// <summary>Where a drop at this point would go.</summary>
    private readonly record struct TrayDropTarget(TrayDrop.Kind Kind, NotifyIcon? Before, int? Gap)
    {
        public static TrayDropTarget None => new(TrayDrop.Kind.None, null, null);
    }

    /// <summary>
    /// The menu while it is open (or the chevron, which stands for it), else the bar, else nowhere.
    /// All in physical pixels, as <see cref="PointerIsOverTrayOverflow"/> reads them.
    /// </summary>
    /// <remarks>
    /// The chevron is looked for where it is drawn. From the menu it is drawn a slot further out
    /// while the row has made room, so while the pointer is on it both places count, or reaching
    /// it would put it back and take it away again on alternate moves.
    /// </remarks>
    private TrayDropTarget TrayDropAt(Point cursor, TrayPress drag)
    {
        // Also with "show all" on: a drop here then ends it (TrayChoice.PutBehindChevron).
        if (TrayOverflowPopup.IsOpen && PointerIsOverTrayOverflow())
            return new TrayDropTarget(TrayDrop.Kind.Overflow, null, null);

        TrayRow row = MeasureTrayRow();

        if (TrayChevron.IsVisible)
        {
            Rect laid = LayoutScreenRect(TrayChevron);
            Rect shifted = laid;
            shifted.Offset(IsVertical ? new Vector(0, -row.Pitch) : new Vector(-row.Pitch, 0));

            Rect drawnAt = _trayChevronShift != 0 ? shifted : laid;
            bool onChevron = drawnAt.Contains(cursor)
                || (drag.FromFlyout && _trayTarget.Kind == TrayDrop.Kind.Overflow && (laid.Contains(cursor) || shifted.Contains(cursor)));

            if (onChevron) return new TrayDropTarget(TrayDrop.Kind.Overflow, null, null);
        }

        double dpi = DpiScale <= 0 ? 1.0 : DpiScale;
        Rect bar = new(Left * dpi, Top * dpi, ActualWidth * dpi, ActualHeight * dpi);
        if (!bar.Contains(cursor)) return TrayDropTarget.None;

        bool fromBar = !drag.FromFlyout && row.Icons.Contains(drag.Icon);
        double along = IsVertical ? cursor.Y : cursor.X;
        int gap = TrayDrop.RoomGapIndex(row.Icons.Count, fromBar, row.FirstSlotStart, row.Pitch, along);

        List<NotifyIcon> others = fromBar ? [.. row.Icons.Where(i => !ReferenceEquals(i, drag.Icon))] : row.Icons;
        NotifyIcon? before = gap < others.Count ? others[gap] : null;
        return new TrayDropTarget(TrayDrop.Kind.Bar, before, gap);
    }

    /// <summary>
    /// Where layout put an element, on screen, leaving out any slide drawn on it: its layout slot
    /// in its parent, which a render transform does not move.
    /// </summary>
    private static Rect LayoutScreenRect(FrameworkElement element)
    {
        if (VisualTreeHelper.GetParent(element) is not UIElement parent) return ScreenRect(element);

        Rect slot = LayoutInformation.GetLayoutSlot(element);
        return new Rect(parent.PointToScreen(slot.TopLeft), parent.PointToScreen(slot.BottomRight));
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
