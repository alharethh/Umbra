using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Core.Wear;

namespace Umbra.Shell.Wear;

/// <summary>
/// The heatmap as a picture to save or paste: the README's layout, with a caption.
/// </summary>
/// <remarks>
/// Rendered from the dataset, never captured from the window. A screenshot of the window is the
/// window's size, the monitor's DPI and whatever crop the user managed; this is the same image on
/// every machine, at exact pixels, so two people's pictures can be put side by side.
///
/// The layout is <c>tools/render-heatmap.cs</c>'s, which made the README picture: the whole strip
/// on top at a fixed width, and under it the leading section magnified twice, because at the
/// width an image is shown in a post the whole strip makes every icon a ten-pixel smudge. Nearest
/// neighbour throughout: this is per-pixel data, and smoothing it would invent wear that was never
/// measured. Below both, one line of caption: how many hours the data covers, the date, and the
/// name in the corner.
///
/// Only wear is drawn. The dataset is the bar's light over time, not its contents, so nothing
/// here names an application, though a pinned icon's outline is legible to anyone who knows it.
/// </remarks>
internal static class HeatmapPicture
{
    /// <summary>The picture's width, in pixels. The README composite's.</summary>
    public const int Width = 1280;

    /// <summary>The space between the whole strip and the magnified section.</summary>
    public const int Gap = 14;

    /// <summary>How much of the leading end is magnified, in source pixels, and by how much.</summary>
    public const int DetailWidth = 640, DetailScale = 2;

    /// <summary>The caption strip's height.</summary>
    public const int CaptionHeight = 40;

    private const double CaptionFontSize = 15;
    private const double CaptionInset = 14;

    // The settings window's text colour, and the logo's ring: the name is the one coloured thing.
    private static readonly Color CaptionText = Color.FromRgb(0xB6, 0xB6, 0xAD);
    private static readonly Color NameColour = Color.FromRgb(0xB0, 0xF7, 0xFB);

    /// <summary>The name drawn in the corner.</summary>
    public const string Name = "Umbra";

    /// <summary>The caption's text, apart from the name.</summary>
    public static string Caption(double elapsedSeconds, DateTime date)
    {
        double hours = Math.Max(0.0, elapsedSeconds) / 3600.0;

        // A decimal where it says something: "0.4 hours" is honest about a thin dataset, while
        // "312.6 hours" is false precision on a number nobody reads past the integer.
        string figure = hours < 10.0
            ? hours.ToString("0.0", CultureInfo.InvariantCulture)
            : hours.ToString("#,0", CultureInfo.InvariantCulture);

        string unit = figure == "1.0" ? "hour" : "hours";

        return string.Create(CultureInfo.InvariantCulture,
            $"Taskbar wear heatmap · {figure} {unit} recorded · {date:yyyy-MM-dd}");
    }

    /// <summary>The name the save dialog offers.</summary>
    public static string FileName(DateTime date) =>
        string.Create(CultureInfo.InvariantCulture, $"umbra-heatmap-{date:yyyy-MM-dd}.png");

    /// <summary>Renders the picture.</summary>
    /// <remarks>
    /// The two strips are laid out here in pixels rather than drawn scaled through WPF. Asked for
    /// nearest-neighbour and aliased edges, <c>RenderTargetBitmap</c> still blended the magnified
    /// section's outermost row and column into the black around it, halving a hot cell at the
    /// leading corner. Only the caption goes through WPF, onto an image already drawn 1:1.
    /// </remarks>
    public static BitmapSource Compose(WearAccumulator accumulator, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(accumulator);

        byte[] source = HeatmapPixels.Bgra(accumulator);
        int sw = accumulator.Width, sh = accumulator.Height;

        // A vertical bar's dataset is tall and thin. Read it a quarter-turn anticlockwise, so its
        // top becomes the left and the leading end is the one magnified either way.
        bool turned = sh > sw;
        int along = turned ? sh : sw, across = turned ? sw : sh;
        int Source(int u, int v) => turned ? (u * sw + (sw - 1 - v)) * 4 : (v * sw + u) * 4;

        int wholeHeight = Math.Max(1, (int)Math.Round(across * (Width / (double)along)));
        int detailWidth = Math.Min(DetailWidth, along);
        int detailTop = wholeHeight + Gap;
        int captionTop = detailTop + across * DetailScale;
        int height = captionTop + CaptionHeight;

        int stride = Width * 4;
        byte[] pixels = new byte[stride * height];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        void Put(int x, int y, int from) => Array.Copy(source, from, pixels, y * stride + x * 4, 4);

        // The whole strip, sampled at each output pixel's centre.
        for (int y = 0; y < wholeHeight; y++)
        {
            int v = Math.Min(across - 1, (int)((y + 0.5) * across / wholeHeight));
            for (int x = 0; x < Width; x++)
                Put(x, y, Source(Math.Min(along - 1, (int)((x + 0.5) * along / Width)), v));
        }

        // The leading section, each cell a DetailScale square.
        for (int y = 0; y < across * DetailScale; y++)
            for (int x = 0; x < detailWidth * DetailScale; x++)
                Put(x, detailTop + y, Source(x / DetailScale, y / DetailScale));

        BitmapSource strips = BitmapSource.Create(Width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

        DrawingVisual visual = new();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Ideal);

        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(strips, new Rect(0, 0, Width, height));

            FormattedText caption = Text(Caption(accumulator.ElapsedSeconds, date), CaptionText, FontWeights.Normal);
            FormattedText name = Text(Name, NameColour, FontWeights.SemiBold);

            dc.DrawText(caption, new Point(CaptionInset, captionTop + (CaptionHeight - caption.Height) / 2));
            dc.DrawText(name, new Point(Width - CaptionInset - name.Width, captionTop + (CaptionHeight - name.Height) / 2));
        }

        RenderTargetBitmap rendered = new(Width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    /// <summary>Writes the picture as PNG.</summary>
    public static void SavePng(BitmapSource picture, Stream output)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        encoder.Save(output);
    }

    private static FormattedText Text(string text, Color colour, FontWeight weight)
    {
        SolidColorBrush brush = new(colour);
        brush.Freeze();

        // pixelsPerDip 1: the picture is laid out in its own pixels, not the monitor's.
        return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            CaptionFontSize, brush, pixelsPerDip: 1.0);
    }
}
