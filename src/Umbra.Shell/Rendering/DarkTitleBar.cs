using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Extends a window's theme into its title bar.
/// </summary>
/// <remarks>
/// <para>
/// The title bar is not part of the window's content. It is drawn by the desktop compositor, which
/// knows nothing about <c>Background</c> — so a fully themed dark window still comes up with a white
/// caption bar. No amount of XAML reaches it; it has to be asked for through DWM.
/// </para>
/// <para>
/// These windows are unconditionally dark, because the bar they configure is black by design for
/// OLED reasons and a light settings window next to it would be the brightest thing on the screen.
/// So the caption is made dark to match the content rather than made to follow the system theme.
/// </para>
/// <para>
/// The colour attributes are Windows 11 only and the call fails harmlessly on anything older,
/// leaving the immersive dark mode flag to do the work on its own. Nothing here is checked for
/// success: every one of them is cosmetic, and a window that opens with the wrong caption colour is
/// better than one that does not open.
/// </para>
/// </remarks>
internal static class DarkTitleBar
{
    /// <summary>Dark mode for the non-client area. 19 on Windows 10 before 20H1, 20 after.</summary>
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeLegacy = 19;

    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int BorderColor = 34;

    /// <summary>Themes the window's caption, now or as soon as it has a handle.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Theme(window);
            return;
        }

        // SourceInitialized rather than Loaded: the handle exists by then and the window has not
        // been presented, so the caption is never painted light first.
        window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;

        window.SourceInitialized -= OnSourceInitialized;
        Theme(window);
    }

    private static void Theme(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        int on = 1;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref on, sizeof(int));
        DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref on, sizeof(int));

        // Taken from the window itself so the caption cannot drift from the content it sits above.
        if (Reference(window.Background) is { } background)
        {
            int caption = ColorRef(background);
            DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));

            int border = ColorRef(background);
            DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));
        }

        if (Reference(window.Foreground) is { } foreground)
        {
            int text = ColorRef(foreground);
            DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
        }
    }

    private static Color? Reference(Brush? brush) =>
        brush is SolidColorBrush solid ? solid.Color : null;

    /// <summary>A Win32 COLORREF, which is 0x00BBGGRR — not the RGB order everything else uses.</summary>
    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
