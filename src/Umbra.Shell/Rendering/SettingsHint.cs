using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Umbra.Core.Layout;
using Rect = System.Windows.Rect;

namespace Umbra.Shell.Rendering;

/// <summary>
/// The one-time note that says where the settings are: "Right-click the bar for settings".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists:</b> the settings are behind a right-click on the bar, and most people never
/// guess that. Shown once, a few seconds after the first launch, beside the bar, and gone on the
/// first click anywhere on the bar or on the note, or after <see cref="Lifetime"/>.
/// </para>
/// <para>
/// <b>A window, not a popup of the bar's.</b> The bar carries <c>WS_EX_NOACTIVATE</c>, and so
/// does this, so showing it never takes the keyboard from whatever the user is typing into. It is
/// topmost like the bar, and a tool window, so it never appears in Alt+Tab.
/// </para>
/// <para>
/// <b>Wear:</b> a few dozen lit pixels for twenty seconds, once in the life of the install.
/// Nothing a duty budget can see.
/// </para>
/// </remarks>
internal sealed class SettingsHint : Window
{
    /// <summary>How long the note stays if nobody clicks.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(20);

    /// <summary>The text, in one place for the tests and the window.</summary>
    public const string Message = "Right-click the bar for settings";

    /// <summary>Space between the note and the bar, in DIP.</summary>
    public const double Gap = 10.0;

    private readonly DispatcherTimer _timer;

    public SettingsHint()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Umbra hint";
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x16)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 9, 14, 9),
            Child = new TextBlock
            {
                Text = Message,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xD8)),
            },
        };

        MouseDown += (_, _) => Close();

        _timer = new DispatcherTimer { Interval = Lifetime };
        _timer.Tick += (_, _) => Close();
        Closed += (_, _) => _timer.Stop();

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        };
    }

    /// <summary>Shows the note beside <paramref name="bar"/>, which is in DIP on the bar's screen.</summary>
    public void ShowBeside(Rect bar, BarEdge edge)
    {
        // Measured before it is shown, so it opens in its final place rather than jumping there.
        ((FrameworkElement)Content).Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = ((FrameworkElement)Content).DesiredSize;

        Point corner = Placement(bar, edge, size);
        Left = corner.X;
        Top = corner.Y;

        Show();
        _timer.Start();
    }

    /// <summary>
    /// Where the note's top-left corner goes: off the bar's inner side, centred along it.
    /// </summary>
    /// <remarks>
    /// The inner side is the one facing the desktop, so the note never covers the bar it is
    /// pointing at and never runs off the screen edge the bar sits on. Centred along the bar
    /// because the right-click works anywhere on it.
    /// </remarks>
    public static Point Placement(Rect bar, BarEdge edge, Size note) => edge switch
    {
        BarEdge.Bottom => new Point(bar.Left + (bar.Width - note.Width) / 2, bar.Top - Gap - note.Height),
        BarEdge.Top => new Point(bar.Left + (bar.Width - note.Width) / 2, bar.Bottom + Gap),
        BarEdge.Left => new Point(bar.Right + Gap, bar.Top + (bar.Height - note.Height) / 2),
        BarEdge.Right => new Point(bar.Left - Gap - note.Width, bar.Top + (bar.Height - note.Height) / 2),
        _ => new Point(bar.Left, bar.Top),
    };

    /// <summary>
    /// Whether to show it now: never shown before, and nothing in the way of the user seeing it.
    /// </summary>
    /// <remarks>
    /// Over a fullscreen game, or on a bar that has faded to black, the note is not shown and not
    /// marked as shown, so it waits for a launch the user will actually see.
    /// </remarks>
    public static bool ShouldShow(bool alreadyShown, bool fullScreenInFront, bool away) =>
        !alreadyShown && !fullScreenInFront && !away;

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
