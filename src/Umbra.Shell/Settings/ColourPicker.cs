using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Umbra.Core.Color;
using Umbra.Shell.Rendering.Themes;
using WpfColor = System.Windows.Media.Color;

namespace Umbra.Shell.Settings;

/// <summary>
/// The panel a colour swatch opens: a saturation and brightness square, a hue strip, a row of
/// presets, and a preview of what the pick will really look like.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hex box stays the truth.</b> This panel only ever writes a new colour into it, through
/// <see cref="Picked"/>, and the settings window's existing path does the rest: a pick is saved
/// and applied exactly as a typed colour is.
/// </para>
/// <para>
/// <b>The hue is the panel's own, not read back from the colour.</b> A grey or black has no hue,
/// so reading it back would snap the strip to red the moment the thumb touched the square's left
/// or bottom edge, and the square would turn red under the pointer.
/// </para>
/// <para>
/// Built in code rather than XAML so that it is one file, like the theme tiles. Every colour of
/// its own chrome is a theme resource, so it follows the window's theme live.
/// </para>
/// </remarks>
internal sealed class ColourPicker : Border
{
    public const double SquareWidth = 216;
    public const double SquareHeight = 136;
    private const double HueHeight = 14;
    private const double KeyStep = 0.02;

    private readonly ColourRole _role;
    private readonly SolidColorBrush _hueFill = new();
    private readonly Canvas _squareThumbLayer = new() { IsHitTestVisible = false };
    private readonly Grid _squareThumb;
    private readonly Canvas _hueThumbLayer = new() { IsHitTestVisible = false };
    private readonly Border _hueThumb;
    private readonly Border _pickedSwatch = PreviewSwatch();
    private readonly TextBlock _pickedHex = PreviewHex();
    private readonly Border _drawnSwatch = PreviewSwatch();
    private readonly TextBlock _drawnHex = PreviewHex();

    private double _h, _s, _v;

    /// <summary>A new colour: on every move of a drag, then once more with <c>final</c> when it ends.</summary>
    public event Action<Rgb, bool>? Picked;

    /// <summary>Esc (undo the picks) or Enter (keep them).</summary>
    public event Action<bool>? Done;

    public ColourPicker(ColourRole role, string title, Rgb initial)
    {
        _role = role;
        ThemeResources.EnsureFor(this);

        Hsv start = Hsv.FromRgb(initial);
        (_h, _s, _v) = (start.H, start.S, start.V);

        SetResourceReference(BackgroundProperty, ThemeResources.MenuSurface);
        SetResourceReference(BorderBrushProperty, ThemeResources.Border);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12);
        Focusable = false;
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black };
        AutomationProperties.SetName(this, title + " colour picker");

        StackPanel stack = new() { Width = SquareWidth };

        TextBlock heading = new() { Text = title, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, ThemeResources.TextStrong);
        stack.Children.Add(heading);

        // ---- the square: the hue, washed to white across, darkened to black downward.
        _squareThumb = Thumb(12);
        _squareThumbLayer.Children.Add(_squareThumb);

        Grid square = new()
        {
            Width = SquareWidth,
            Height = SquareHeight,
            Cursor = Cursors.Cross,
            Focusable = true,
            FocusVisualStyle = null,
            ClipToBounds = true,
        };
        AutomationProperties.SetName(square, "Saturation and brightness");
        square.Children.Add(new Rectangle { Fill = _hueFill, RadiusX = 4, RadiusY = 4 });
        square.Children.Add(new Rectangle
        {
            RadiusX = 4, RadiusY = 4,
            Fill = new LinearGradientBrush(Colors.White, WpfColor.FromArgb(0, 255, 255, 255), 0),
        });
        square.Children.Add(new Rectangle
        {
            RadiusX = 4, RadiusY = 4,
            Fill = new LinearGradientBrush(WpfColor.FromArgb(0, 0, 0, 0), Colors.Black, 90),
        });
        square.Children.Add(_squareThumbLayer);
        Drag(square, p => PickSquare(p.X / SquareWidth, 1 - p.Y / SquareHeight, final: false));
        square.KeyDown += (_, e) => e.Handled = OnSquareKey(e.Key);
        stack.Children.Add(square);

        // ---- the hue strip.
        _hueThumb = new Border
        {
            Width = 6, Height = HueHeight + 4,
            CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9, Color = Colors.Black },
        };
        _hueThumbLayer.Children.Add(_hueThumb);

        GradientStopCollection stops = [];
        for (int i = 0; i <= 6; i++)
        {
            Rgb c = new Hsv(i * 60, 1, 1).ToRgb();
            stops.Add(new GradientStop(WpfColor.FromRgb(c.R, c.G, c.B), i / 6.0));
        }
        Grid hue = new()
        {
            Height = HueHeight + 4,
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = null,
            Background = Brushes.Transparent,
        };
        AutomationProperties.SetName(hue, "Hue");
        hue.Children.Add(new Border
        {
            Height = HueHeight,
            CornerRadius = new CornerRadius(HueHeight / 2),
            Background = new LinearGradientBrush(stops, 0),
        });
        hue.Children.Add(_hueThumbLayer);
        Drag(hue, p => PickHue(p.X / SquareWidth * 360, final: false));
        hue.KeyDown += (_, e) => e.Handled = OnHueKey(e.Key);
        stack.Children.Add(hue);

        // ---- presets.
        UniformGrid presets = new() { Columns = 6, Margin = new Thickness(-2, 10, -2, 0) };
        foreach (Rgb preset in ColourPick.Presets(role))
            presets.Children.Add(PresetButton(preset));
        stack.Children.Add(presets);

        // ---- the preview: what was picked, and what will be drawn when that differs.
        Grid preview = new() { Margin = new Thickness(0, 12, 0, 0) };
        preview.ColumnDefinitions.Add(new ColumnDefinition());
        preview.ColumnDefinitions.Add(new ColumnDefinition());
        preview.Children.Add(PreviewCell("Picked", _pickedSwatch, _pickedHex, 0));
        if (ColourPick.DrawnLabel(role) is { } drawnLabel)
            preview.Children.Add(PreviewCell(drawnLabel, _drawnSwatch, _drawnHex, 1));
        stack.Children.Add(preview);

        if (ColourPick.DrawnNote(role) is { } note)
        {
            TextBlock hint = new() { Text = note, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, ThemeResources.TextHint);
            stack.Children.Add(hint);
        }

        Child = stack;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter)
            {
                Done?.Invoke(e.Key == Key.Enter);
                e.Handled = true;
            }
        };

        Show();
    }

    /// <summary>The colour the panel holds now.</summary>
    public Rgb Colour => new Hsv(_h, _s, _v).ToRgb();

    /// <summary>The colour its "drawn" swatch shows: what the bar or window will really draw.</summary>
    public Rgb DrawnShown => ((SolidColorBrush)_drawnSwatch.Background) is { } b ? new Rgb(b.Color.R, b.Color.G, b.Color.B) : default;

    /// <summary>The colour its "picked" swatch shows.</summary>
    public Rgb PickedShown => ((SolidColorBrush)_pickedSwatch.Background) is { } b ? new Rgb(b.Color.R, b.Color.G, b.Color.B) : default;

    /// <summary>The hue the strip is set to, kept through greys.</summary>
    public double Hue => _h;

    /// <summary>A point in the square: saturation across, brightness up, both 0 to 1.</summary>
    public void PickSquare(double saturation, double value, bool final)
    {
        _s = Math.Clamp(saturation, 0, 1);
        _v = Math.Clamp(value, 0, 1);
        Show();
        Picked?.Invoke(Colour, final);
    }

    /// <summary>A hue in degrees, kept in [0, 360).</summary>
    public void PickHue(double degrees, bool final)
    {
        // The strip's right end is 360, which is red again; keep it just short so the thumb
        // stays where it was put rather than jumping back to the left end.
        _h = Math.Clamp(degrees, 0, 359.999);
        Show();
        Picked?.Invoke(Colour, final);
    }

    /// <summary>One of the presets. Takes its hue, unlike a pick in the square.</summary>
    public void PickPreset(Rgb colour)
    {
        Hsv hsv = Hsv.FromRgb(colour);

        // A grey preset has no hue; keep the strip where it is.
        if (hsv.S > 0) _h = hsv.H;
        (_s, _v) = (hsv.S, hsv.V);
        Show();
        Picked?.Invoke(colour, true);
    }

    private void Show()
    {
        Rgb pure = new Hsv(_h, 1, 1).ToRgb();
        _hueFill.Color = WpfColor.FromRgb(pure.R, pure.G, pure.B);

        Canvas.SetLeft(_squareThumb, _s * SquareWidth - _squareThumb.Width / 2);
        Canvas.SetTop(_squareThumb, (1 - _v) * SquareHeight - _squareThumb.Height / 2);
        Canvas.SetLeft(_hueThumb, _h / 360 * SquareWidth - _hueThumb.Width / 2);
        Canvas.SetTop(_hueThumb, 0);

        Rgb picked = Colour;
        Rgb drawn = ColourPick.Drawn(_role, picked);
        _pickedSwatch.Background = Brush(picked);
        _pickedHex.Text = picked.ToHex();
        _drawnSwatch.Background = Brush(drawn);
        _drawnHex.Text = drawn.ToHex();
    }

    private bool OnSquareKey(Key key)
    {
        switch (key)
        {
            case Key.Left: PickSquare(_s - KeyStep, _v, final: true); return true;
            case Key.Right: PickSquare(_s + KeyStep, _v, final: true); return true;
            case Key.Up: PickSquare(_s, _v + KeyStep, final: true); return true;
            case Key.Down: PickSquare(_s, _v - KeyStep, final: true); return true;
            default: return false;
        }
    }

    private bool OnHueKey(Key key)
    {
        switch (key)
        {
            case Key.Left or Key.Down: PickHue((_h - 3 + 360) % 360, final: true); return true;
            case Key.Right or Key.Up: PickHue((_h + 3) % 360, final: true); return true;
            default: return false;
        }
    }

    /// <summary>Press, drag and release on an element, reported in its own coordinates.</summary>
    private void Drag(FrameworkElement area, Action<Point> at)
    {
        area.MouseLeftButtonDown += (_, e) =>
        {
            area.Focus();
            area.CaptureMouse();
            at(e.GetPosition(area));
            e.Handled = true;
        };
        area.MouseMove += (_, e) =>
        {
            if (area.IsMouseCaptured) at(e.GetPosition(area));
        };
        area.MouseLeftButtonUp += (_, e) =>
        {
            if (!area.IsMouseCaptured) return;
            area.ReleaseMouseCapture();
            Picked?.Invoke(Colour, true);
            e.Handled = true;
        };
    }

    private Button PresetButton(Rgb colour)
    {
        Button button = new()
        {
            Height = 22,
            Margin = new Thickness(2),
            Background = Brush(colour),
            Cursor = Cursors.Hand,
            ToolTip = colour.ToHex(),
            Template = SwatchTemplate(),
        };
        AutomationProperties.SetName(button, colour.ToHex());
        button.Click += (_, _) => PickPreset(colour);
        return button;
    }

    /// <summary>A plain rounded swatch that shows hover and keyboard focus with the window accent.</summary>
    private static ControlTemplate SwatchTemplate()
    {
        FrameworkElementFactory border = new(typeof(Border), "Face");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background))
        {
            RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
        });
        border.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);

        ControlTemplate template = new(typeof(Button)) { VisualTree = border };

        Trigger hover = new() { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(ThemeResources.Accent), "Face"));
        template.Triggers.Add(hover);

        Trigger focus = new() { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(ThemeResources.Accent), "Face"));
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Face"));
        template.Triggers.Add(focus);

        return template;
    }

    private static Grid Thumb(double size)
    {
        // White ring over a dark one, so it shows on any colour in the square.
        Grid thumb = new() { Width = size, Height = size };
        thumb.Children.Add(new Ellipse { Stroke = new SolidColorBrush(WpfColor.FromArgb(160, 0, 0, 0)), StrokeThickness = 3 });
        thumb.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 1.5, Margin = new Thickness(0.75) });
        return thumb;
    }

    private static Border PreviewSwatch()
    {
        Border swatch = new() { Width = 30, Height = 20, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        swatch.SetResourceReference(BorderBrushProperty, ThemeResources.Border);
        return swatch;
    }

    private static TextBlock PreviewHex()
    {
        TextBlock hex = new() { FontFamily = new FontFamily("Consolas"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        hex.SetResourceReference(TextBlock.ForegroundProperty, ThemeResources.Text);
        return hex;
    }

    private static StackPanel PreviewCell(string label, Border swatch, TextBlock hex, int column)
    {
        TextBlock caption = new() { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, ThemeResources.TextHint);

        StackPanel row = new() { Orientation = Orientation.Horizontal };
        row.Children.Add(swatch);
        row.Children.Add(hex);

        StackPanel cell = new();
        cell.Children.Add(caption);
        cell.Children.Add(row);
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static SolidColorBrush Brush(Rgb colour)
    {
        SolidColorBrush brush = new(WpfColor.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }
}
