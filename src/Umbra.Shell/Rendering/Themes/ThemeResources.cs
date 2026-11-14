using System.Windows;
using System.Windows.Media;
using Umbra.Core.Color;

namespace Umbra.Shell.Rendering.Themes;

/// <summary>
/// Puts a theme's window palette where every Umbra window and panel finds it: application-level
/// resources, referenced with <c>DynamicResource</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Application resources, because a context menu cannot find a window's.</b> A menu lives in
/// its own popup tree and its lookup does not climb into the window that owns it (the trap
/// <c>BarWindow.MenuStyle</c> exists for), but every lookup ends at the application. So the
/// menus, the window picker, the tray flyout, the heatmap and the settings window all follow one
/// dictionary, and replacing a brush there repaints them all at once, live.
/// </para>
/// <para>
/// <b>New frozen brushes, never a mutated one.</b> Changing the colour of a shared brush would
/// repaint without a lookup, but an unfrozen brush belongs to the thread that made it, and the
/// test suite builds windows on threads of their own.
/// </para>
/// </remarks>
internal static class ThemeResources
{
    public const string Background = "Umbra.Background";
    public const string Surface = "Umbra.Surface";
    public const string MenuSurface = "Umbra.MenuSurface";
    public const string NavHover = "Umbra.NavHover";
    public const string Control = "Umbra.Control";
    public const string NavSelected = "Umbra.NavSelected";
    public const string ControlHover = "Umbra.ControlHover";
    public const string Divider = "Umbra.Divider";
    public const string Highlight = "Umbra.Highlight";
    public const string Border = "Umbra.Border";
    public const string Thumb = "Umbra.Thumb";
    public const string BorderHover = "Umbra.BorderHover";
    public const string TextDim = "Umbra.TextDim";
    public const string TextHint = "Umbra.TextHint";
    public const string Glyph = "Umbra.Glyph";
    public const string Text = "Umbra.Text";
    public const string TextItem = "Umbra.TextItem";
    public const string TextStrong = "Umbra.TextStrong";
    public const string Accent = "Umbra.Accent";
    public const string Warning = "Umbra.Warning";

    /// <summary>Writes <paramref name="palette"/> into <paramref name="resources"/>.</summary>
    public static void Apply(ResourceDictionary resources, WindowPalette palette)
    {
        Set(resources, Background, palette.Background);
        Set(resources, Surface, palette.Surface);
        // The menus were drawn very slightly translucent, and still are.
        Set(resources, MenuSurface, palette.Surface, 0xF2);
        Set(resources, NavHover, palette.NavHover);
        Set(resources, Control, palette.Control);
        Set(resources, NavSelected, palette.NavSelected);
        Set(resources, ControlHover, palette.ControlHover);
        Set(resources, Divider, palette.Divider);
        Set(resources, Highlight, palette.Highlight);
        Set(resources, Border, palette.Border);
        Set(resources, Thumb, palette.Thumb);
        Set(resources, BorderHover, palette.BorderHover);
        Set(resources, TextDim, palette.TextDim);
        Set(resources, TextHint, palette.TextHint);
        Set(resources, Glyph, palette.Glyph);
        Set(resources, Text, palette.Text);
        Set(resources, TextItem, palette.TextItem);
        Set(resources, TextStrong, palette.TextStrong);
        Set(resources, Accent, palette.Accent);
        Set(resources, Warning, palette.Warning);
    }

    /// <summary>
    /// Applies a theme to the running application and re-colours every open window's caption,
    /// which the compositor draws and no resource reaches.
    /// </summary>
    public static void ApplyToApplication(ThemeColours colours)
    {
        if (Application.Current is not { } app) return;

        Apply(app.Resources, UmbraTheme.Palette(colours));

        foreach (Window window in app.Windows) DarkTitleBar.Apply(window);
    }

    /// <summary>
    /// Gives a window the default palette when there is no application to supply one, which is
    /// the case in the test suite. In the running bar this does nothing.
    /// </summary>
    public static void EnsureFor(FrameworkElement element)
    {
        if (Application.Current?.Resources.Contains(Background) == true) return;

        Apply(element.Resources, UmbraTheme.Palette(UmbraTheme.Default.Colours));
    }

    private static void Set(ResourceDictionary resources, string key, Rgb colour, byte alpha = 0xFF)
    {
        SolidColorBrush brush = new(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
        brush.Freeze();
        resources[key] = brush;
    }
}
