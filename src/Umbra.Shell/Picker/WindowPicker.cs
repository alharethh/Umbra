using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;
using Umbra.Core.Layout;
using Umbra.Core.Tiers;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Tasks;
using WpfRect = System.Windows.Rect;

namespace Umbra.Shell.Picker;

/// <summary>
/// The window picker: one live thumbnail per window of an application, each closeable on its own.
/// </summary>
/// <remarks>
/// <para>
/// This is what an application with several windows does on click, replacing the cycle-to-next
/// behaviour that came before it. Cycling gave no way to reach a specific window except by clicking
/// repeatedly and watching what came up, and no way to close one without first focusing it.
/// </para>
/// <para>
/// <b>It never takes focus.</b> The window carries <c>WS_EX_NOACTIVATE</c>, as the bar does, for
/// the same reason: activating would make the picker the foreground window, which would blank the
/// running indicator and lose track of which window the user was actually looking at before they
/// opened it. Clicks still arrive; only activation is refused.
/// </para>
/// <para>
/// Because it never activates, it also never gets a deactivated event to close on. It therefore
/// watches the cursor while it is open, and begins fading the moment the pointer is away from both
/// itself and the bar — the same moment the bar starts dimming, and over the same
/// <see cref="FadeOut"/>, so the two leave together. That timer runs only while the picker is up,
/// so it does not add to the permanent wake-up budget §6.6 is careful about.
/// </para>
/// </remarks>
internal sealed partial class WindowPicker : Window
{
    /// <summary>
    /// How long the picker takes to fade once the pointer has left it and the bar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bar's own fall, so the two go together.</b> It used to sit at full opacity for 600 ms
    /// and then vanish in a single frame, while the bar underneath had already been dimming the
    /// whole time — the preview outlived the thing it belonged to, and the disappearance was a cut
    /// rather than a fade.
    /// </para>
    /// <para>
    /// <see cref="TierController.ReleaseSeconds"/> is one tier <i>step</i>, and the bar falls two
    /// of them — Tier 2 to Tier 0 — so the picker's fade is twice it. Taking the number from the
    /// tier system rather than writing 800 here means the two cannot drift apart later.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan FadeOut =
        TimeSpan.FromSeconds(TierController.ReleaseSeconds * 2.0);

    /// <summary>How long it takes to come back if the pointer returns mid-fade.</summary>
    /// <remarks>
    /// The bar's attack, for the same reason, and quick: a picker that crawls back to full opacity
    /// reads as lag on a pointer that never really left.
    /// </remarks>
    private static readonly TimeSpan FadeIn = TimeSpan.FromSeconds(TierController.AttackSeconds);

    private const double ColumnWidth = 200.0;
    private const double ThumbnailHeight = 116.0;
    private const double HeaderHeight = 22.0;
    private const double ColumnGap = 8.0;
    private const double OuterPadding = 12.0;

    /// <summary>Beyond this the picker would be wider than most screens and stops being a picker.</summary>
    private const int MaxColumns = 8;

    /// <summary>Border plus padding the hover frame adds around each column, both sides.</summary>
    private const double FrameChrome = 8.0;

    /// <summary>How long the press is shown before the picker acts, in milliseconds.</summary>
    private const int PressHoldMilliseconds = 110;

    private readonly List<Column> _columns = [];
    private readonly TaskbarSlot _slot;
    private readonly WpfRect _barScreenDip;
    private readonly double _dpiScale;
    private readonly DispatcherTimer _watch;

    private DateTime _awaySince = DateTime.MaxValue;
    private IntPtr _handle;

    public WindowPicker(TaskbarSlot slot, WpfRect anchorScreenDip, WpfRect barScreenDip,
                        BarEdge edge, double dpiScale)
    {
        _slot = slot;
        _barScreenDip = barScreenDip;
        _dpiScale = dpiScale <= 0 ? 1.0 : dpiScale;

        InitializeComponent();
        Build();
        Position(anchorScreenDip, edge);

        _watch = new DispatcherTimer(DispatcherPriority.Background)
        {
            // 30 Hz rather than 10. At 100 ms the picker could sit lit for a tenth of a second
            // after the bar had begun to dim, which is visible against an 800 ms fade. It runs
            // only while the picker is up, so it still costs nothing in steady state (§6.6).
            Interval = TimeSpan.FromMilliseconds(32),
        };
        _watch.Tick += OnWatchTick;

        Loaded += OnLoaded;
    }

    /// <summary>Raised when a window is chosen, so the bar can drop its reference.</summary>
    public event Action? Dismissed;

    /// <summary>The application this picker belongs to, so a second click on it can toggle.</summary>
    public string SlotKey => _slot.Key;

    /// <summary>
    /// Whether the pointer is on the picker, by the same reckoning the dismissal watch uses.
    /// </summary>
    /// <remarks>
    /// Shared with the tier system, which needs it for a different reason: reaching for a
    /// thumbnail takes the pointer off the bar, and without this the icons underneath grey out
    /// while the user is looking at them. Deliberately the same margin as the dismissal test, so
    /// the picker cannot be lit and dismissing at the same time.
    /// </remarks>
    public bool PointerIsInside()
    {
        if (!GetCursorPos(out NativePoint native)) return false;

        Point cursor = new(native.X / _dpiScale, native.Y / _dpiScale);

        return Inflate(new WpfRect(Left, Top, Width, Height), 12).Contains(cursor);
    }

    /// <summary>Builds one column per window: a header strip, then a box for the thumbnail.</summary>
    /// <remarks>
    /// The header is above the thumbnail rather than over it because DWM composites the thumbnail
    /// on top of everything this window draws — a close button inside that rectangle would be
    /// invisible while remaining perfectly clickable, which is the worst of both.
    /// </remarks>
    private void Build()
    {
        Columns.Children.Clear();
        _columns.Clear();

        foreach (ApplicationWindow window in _slot.Windows.Take(MaxColumns))
        {
            var host = new Border
            {
                Width = ColumnWidth,
                Height = ThumbnailHeight,
                Background = Brushes.Black,
                Cursor = Cursors.Hand,
            };

            // The hover frame sits in the padding around the thumbnail, which is the only part of
            // the column DWM does not draw over. A background fill would be almost entirely hidden
            // behind the composited picture, so the feedback is a border and a brighter title.
            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3),
                Background = Brushes.Transparent,
            };

            // The fallback for a minimised window, which DWM has no live surface for. Replaced by
            // the thumbnail when there is one.
            host.Child = new Image
            {
                Source = _slot.Icon,
                Width = 32,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.55,
            };

            var title = new TextBlock
            {
                Text = window.Title ?? _slot.Title,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 4, 0),
            };

            var close = new Button
            {
                Content = "✕",
                Style = (Style)FindResource("PickerCloseButton"),
                ToolTip = "Close this window",
            };

            var header = new Grid { Height = HeaderHeight };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(title, 0);
            Grid.SetColumn(close, 1);
            header.Children.Add(title);
            header.Children.Add(close);

            var inner = new StackPanel();
            inner.Children.Add(header);
            inner.Children.Add(host);
            frame.Child = inner;

            var column = new StackPanel { Width = ColumnWidth + FrameChrome };
            column.Children.Add(frame);

            var entry = new Column(window, host, column) { Frame = frame, Title = title };

            frame.MouseEnter += (_, _) => SetHover(entry, true);
            frame.MouseLeave += (_, _) => { SetHover(entry, false); SetPressed(entry, false); };

            host.MouseLeftButtonDown += (_, e) => { e.Handled = true; SetPressed(entry, true); };
            host.MouseLeftButtonUp += (_, e) => { e.Handled = true; Choose(entry); };
            close.Click += (_, e) => { e.Handled = true; CloseWindow(entry); };

            _columns.Add(entry);
            Columns.Children.Add(column);
        }

        Relayout();
        Height = OuterPadding * 2 + HeaderHeight + ThumbnailHeight + FrameChrome + 2;
    }

    /// <summary>
    /// Resizes the window to its current column count and re-places the surviving thumbnails.
    /// </summary>
    /// <remarks>
    /// The gap belongs <i>between</i> columns, never after the last one — a trailing margin makes
    /// the panel one gap wider than the width computed here and crops the final column against the
    /// window edge. Because columns are also removed at runtime, the margins have to be reassigned
    /// every time rather than decided once while building.
    /// </remarks>
    private void Relayout()
    {
        for (int i = 0; i < _columns.Count; i++)
        {
            bool last = i == _columns.Count - 1;
            _columns[i].Panel.Margin = new Thickness(0, 0, last ? 0 : ColumnGap, 0);
        }

        int count = _columns.Count;
        Width = OuterPadding * 2 + count * (ColumnWidth + FrameChrome)
              + Math.Max(0, count - 1) * ColumnGap + 2;

        // Layout has to settle before a thumbnail can be placed against its new position, so this
        // waits a beat rather than measuring the arrangement it is in the middle of replacing.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            foreach (Column column in _columns)
                if (column.Thumbnail is { } thumbnail) Place(column, thumbnail);
        });
    }

    /// <summary>Puts the picker beside the bar, centred on the slot and kept on screen.</summary>
    private void Position(WpfRect anchor, BarEdge edge)
    {
        double left, top;

        if (edge.IsVertical())
        {
            left = edge == BarEdge.Left ? _barScreenDip.Right + 8 : _barScreenDip.Left - Width - 8;
            top = anchor.Top + anchor.Height / 2 - Height / 2;
        }
        else
        {
            left = anchor.Left + anchor.Width / 2 - Width / 2;
            top = edge == BarEdge.Top ? _barScreenDip.Bottom + 8 : _barScreenDip.Top - Height - 8;
        }

        // Clamp to the work area, which already excludes the bar's own reserved strip.
        WpfRect work = SystemParameters.WorkArea;
        Left = Math.Clamp(left, work.Left + 4, Math.Max(work.Left + 4, work.Right - Width - 4));
        Top = Math.Clamp(top, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - Height - 4));
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;

        BringUpLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"picker window: {Left:F1},{Top:F1} {Width:F0}x{Height:F0} DIP | " +
            $"{_columns.Count} columns | dpi {_dpiScale:F2}"));

        // Never take focus, and never appear in Alt+Tab. Same reasoning as the bar's own styles.
        int styles = GetWindowLong(_handle, GwlExStyle);
        SetWindowLong(_handle, GwlExStyle, styles | WsExNoActivate | WsExToolWindow);

        // Deferred to after the layout pass. TransformToAncestor and ActualWidth are both read
        // when placing a thumbnail, and at Loaded the arrange pass has not necessarily produced
        // final values -- which would put every thumbnail at the origin, overlapping, at the wrong
        // size. It would look like a DWM problem and be a timing one.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            RegisterThumbnails();
            _watch.Start();
        });
    }

    /// <summary>
    /// Registers each thumbnail and places it in its host's rectangle, converted to physical pixels.
    /// </summary>
    private void RegisterThumbnails()
    {
        foreach (Column column in _columns)
        {
            column.Thumbnail?.Dispose();
            column.Thumbnail = null;

            // Minimised windows are registered too. DWM usually still holds the last composed
            // frame for one, so it previews its actual contents rather than its icon -- which is
            // what the Windows taskbar shows and what makes a preview worth having. Skipping them
            // on IsMinimized meant every minimised window previewed as a grey app icon.
            DwmThumbnail? thumbnail = DwmThumbnail.Register(_handle, column.Window.Handle);
            if (thumbnail is null || !thumbnail.HasContent)
            {
                thumbnail?.Dispose();
                continue;
            }

            column.Thumbnail = thumbnail;
            Place(column, thumbnail);

            BringUpLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"  thumbnail: source {thumbnail.SourceSize.Width}x{thumbnail.SourceSize.Height} px " +
                $"| '{column.Window.Title}'"));
        }
    }

    private void Place(Column column, DwmThumbnail thumbnail)
    {
        // Where the host box sits inside this window, in DIP.
        GeneralTransform transform = column.Host.TransformToAncestor(this);
        Point origin = transform.Transform(new Point(0, 0));

        (double x, double y, double w, double h) =
            thumbnail.Fit(column.Host.ActualWidth, column.Host.ActualHeight);

        // DWM wants physical pixels relative to the client area (DwmThumbnail, constraint 2).
        int left = (int)Math.Round((origin.X + x) * _dpiScale);
        int top = (int)Math.Round((origin.Y + y) * _dpiScale);
        int right = left + (int)Math.Round(w * _dpiScale);
        int bottom = top + (int)Math.Round(h * _dpiScale);

        thumbnail.Place(left, top, right, bottom);

        // The placeholder icon would show through wherever the thumbnail does not reach.
        if (column.Host.Child is Image image) image.Visibility = Visibility.Collapsed;
    }

    /// <summary>Lights the frame and title of the column under the pointer.</summary>
    private static void SetHover(Column column, bool hovered)
    {
        if (column.Frame is { } frame)
        {
            frame.BorderBrush = hovered
                ? new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D))
                : Brushes.Transparent;
        }

        if (column.Title is { } title)
        {
            title.Foreground = new SolidColorBrush(hovered
                ? Color.FromRgb(0xB3, 0xB3, 0xB3)
                : Color.FromRgb(0x66, 0x66, 0x66));
        }
    }

    /// <summary>
    /// Press feedback: the thumbnail dips in opacity while the button is held.
    /// </summary>
    /// <remarks>
    /// Scaling the element would do nothing — DWM draws the thumbnail into a rectangle of its own,
    /// over our content, so a <c>RenderTransform</c> moves an invisible box. Opacity is a property
    /// DWM understands, so that is what the press animates.
    /// </remarks>
    private static void SetPressed(Column column, bool pressed)
    {
        column.Thumbnail?.SetOpacity(pressed ? (byte)150 : (byte)255);

        if (column.Frame is { } frame)
        {
            frame.BorderBrush = new SolidColorBrush(pressed
                ? Color.FromRgb(0x80, 0x80, 0x80)
                : Color.FromRgb(0x4D, 0x4D, 0x4D));
        }
    }

    /// <summary>
    /// Activates the chosen window, after letting the press register visually.
    /// </summary>
    /// <remarks>
    /// The picker would otherwise vanish on the same frame as the click, so the feedback would
    /// never be seen. <see cref="PressHoldMilliseconds"/> is short enough to read as immediate and
    /// long enough to be a response rather than a flicker.
    /// </remarks>
    private void Choose(Column column)
    {
        SetPressed(column, true);

        DispatcherTimer settle = new(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(PressHoldMilliseconds),
        };

        settle.Tick += (_, _) =>
        {
            settle.Stop();
            column.Window.BringToFront();
            Dismiss();
        };

        settle.Start();
    }

    private void CloseWindow(Column column)
    {
        // Through WindowSafety rather than ApplicationWindow.Close, so a shell window in an
        // explorer.exe group is refused rather than closed. See WindowSafety's remarks.
        if (!Umbra.Shell.Tasks.WindowSafety.TryClose(column.Window)) return;

        column.Thumbnail?.Dispose();
        column.Thumbnail = null;
        Columns.Children.Remove(column.Panel);
        _columns.Remove(column);

        // One window left is not a picker any more — the bar handles that case directly.
        if (_columns.Count <= 1)
        {
            Dismiss();
            return;
        }

        Relayout();
    }

    /// <summary>Closes once the pointer has been away from both the picker and the bar long enough.</summary>
    private void OnWatchTick(object? sender, EventArgs e)
    {
        // A window can be closed from anywhere -- its own close button, a keyboard shortcut, a
        // crash -- and its thumbnail would sit there showing the last frame DWM had. Prune first,
        // so the picker never offers a window that is not there.
        if (PruneClosedWindows()) return;

        if (!GetCursorPos(out NativePoint native))
        {
            BringUpLog.Write("picker watch: GetCursorPos failed");
            return;
        }

        var cursor = new Point(native.X / _dpiScale, native.Y / _dpiScale);
        var self = new WpfRect(Left, Top, Width, Height);

        // A margin around the picker so a pointer skimming its edge on the way in does not dismiss
        // it, and around the bar so travelling between the two is not a dismissal either.
        bool inside = Inflate(self, 12).Contains(cursor) || Inflate(_barScreenDip, 12).Contains(cursor);

        if (inside)
        {
            CancelFade();
            _awaySince = DateTime.MaxValue;
            return;
        }

        // The moment the pointer is away from both, not after a wait: the bar starts dimming on
        // the same condition, and anything held back here is the preview outliving it.
        if (_awaySince != DateTime.MaxValue) return;

        _awaySince = DateTime.UtcNow;
        BeginFade();
    }

    /// <summary>Drops columns whose window has gone. Returns true when the picker closed.</summary>
    private bool PruneClosedWindows()
    {
        Column[] dead = [.. _columns.Where(c => !IsWindow(c.Window.Handle))];
        if (dead.Length == 0) return false;

        foreach (Column column in dead)
        {
            column.Thumbnail?.Dispose();
            column.Thumbnail = null;
            Columns.Children.Remove(column.Panel);
            _columns.Remove(column);
        }

        if (_columns.Count <= 1)
        {
            Dismiss();
            return true;
        }

        Relayout();
        return false;
    }

    private static WpfRect Inflate(WpfRect rect, double by) =>
        new(rect.Left - by, rect.Top - by, rect.Width + by * 2, rect.Height + by * 2);

    /// <summary>Closes the picker, releasing every thumbnail.</summary>
    /// <summary>
    /// Fades the picker out and closes it when it has gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Quadratic ease-in, which is <see cref="TierEasing.Release"/>'s curve — the bar dims on that
    /// shape, and a linear fade beside it reads as two things leaving at different speeds even
    /// when they finish together.
    /// </para>
    /// <para>
    /// <b>Closed from the animation's completion rather than from a second timer.</b> One clock
    /// owns the disappearance, so there is no arrangement where the window is disposed while still
    /// visible, or lingers invisibly holding its DWM thumbnail registrations.
    /// </para>
    /// </remarks>
    private void BeginFade()
    {
        if (_fading) return;

        _fading = true;

        DoubleAnimation fade = new()
        {
            To = 0.0,
            Duration = FadeOut,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };

        fade.Completed += (_, _) =>
        {
            if (_fading) Dismiss();
        };

        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Brings it back if the pointer returned before it had gone.</summary>
    /// <remarks>
    /// A second animation rather than an assignment to <c>Opacity</c>: a running animation holds
    /// the property, so setting it directly while one is in flight does nothing visible. Handing
    /// the fade a new target is what stops it, and the flag is what stops its <c>Completed</c>
    /// handler closing a picker that is on its way back.
    /// </remarks>
    private void CancelFade()
    {
        if (!_fading) return;

        _fading = false;

        DoubleAnimation back = new()
        {
            To = 1.0,
            Duration = FadeIn,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        BeginAnimation(OpacityProperty, back);
    }

    private bool _fading;

    public void Dismiss()
    {
        _watch.Stop();
        _watch.Tick -= OnWatchTick;

        foreach (Column column in _columns)
        {
            column.Thumbnail?.Dispose();
            column.Thumbnail = null;
        }

        Dismissed?.Invoke();

        try
        {
            Close();
        }
        catch (InvalidOperationException ex)
        {
            BringUpLog.Write(ex, "window picker close failed");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Belt and braces: an unregistered thumbnail outlives the window and leaks a DWM handle.
        foreach (Column column in _columns) column.Thumbnail?.Dispose();
        _columns.Clear();

        base.OnClosed(e);
    }

    private sealed class Column(ApplicationWindow window, Border host, StackPanel panel)
    {
        public ApplicationWindow Window { get; } = window;
        public Border Host { get; } = host;
        public StackPanel Panel { get; } = panel;
        public DwmThumbnail? Thumbnail { get; set; }

        /// <summary>The hover frame around this column.</summary>
        public Border? Frame { get; init; }

        /// <summary>The title, brightened on hover.</summary>
        public TextBlock? Title { get; init; }
    }

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
