#!/usr/bin/env dotnet run
// Renders the wear heatmap from a saved dataset, without running the bar.
//
//   dotnet run tools/render-heatmap.cs [wear.dat] [out.png]
//
// Defaults to %LOCALAPPDATA%\Umbra\wear.dat and docs/media/heatmap.png.
//
// The heatmap window does the same drawing from live memory; this exists because the README needs
// a picture of it and a screenshot of a window is a screenshot of a window — cropped by hand,
// scaled by whatever took it, and impossible to reproduce when the data moves on. Reading the
// dataset straight off disk gives the real numbers at exact pixel size, and re-running it after
// another week of accumulation produces the same image with more in it.
//
// The scale is logarithmic and that is not cosmetic: the bar spans an unlit background at exactly
// zero and lit icon interiors several orders of magnitude above it, so a linear ramp is a few
// bright glyphs on black and hides everything the heatmap exists to show.
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishTrimmed=false
#:property Nullable=enable
#:property ImplicitUsings=enable
#:project ../src/Umbra.Core/Umbra.Core.csproj

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Core.Wear;

string source = args.Length > 0
    ? args[0]
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Umbra", "wear.dat");

string target = args.Length > 1 ? args[1] : Path.Combine("docs", "media", "heatmap.png");

if (!File.Exists(source)) { Console.Error.WriteLine($"no dataset at {source}"); return 1; }

WearAccumulator accumulator;

using (FileStream file = File.OpenRead(source))
{
    WearAccumulator? loaded = WearStore.Load(file);
    if (loaded is null) { Console.Error.WriteLine($"{source} is not a readable wear dataset"); return 1; }
    accumulator = loaded;
}

double hours = accumulator.ElapsedSeconds / 3600.0;
int lit = 0;
foreach (double cell in accumulator.Cells) if (cell > 0.0) lit++;

Console.WriteLine($"{accumulator.Width}x{accumulator.Height}, n={accumulator.Exponent}, " +
                  $"{hours:F2} h accumulated, peak {accumulator.Peak:G6}, " +
                  $"{lit * 100.0 / accumulator.Cells.Length:F2}% of cells have any wear at all");

if (hours < 1.0)
    Console.WriteLine("NOTE: under an hour of data. Fine for checking the rendering, thin for a README.");

BitmapSource image = Render(accumulator);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);

using (FileStream output = File.Create(target))
{
    PngBitmapEncoder encoder = new();
    encoder.Frames.Add(BitmapFrame.Create(image));
    encoder.Save(output);
}

Console.WriteLine($"wrote {target} ({image.PixelWidth}x{image.PixelHeight})");

// <b>And a composite, because the raw strip is unreadable where it will be shown.</b> The bar is
// 2560x60; a README scales that to about 900 px wide and 21 tall, where every icon is a ten-pixel
// smudge. So: the whole strip on top, at half size, and beneath it the leading section at 2x so
// individual icons can actually be read. Nearest-neighbour on the zoom — this is per-pixel data
// and smoothing it would invent wear that was never measured.
string composite = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(target))!,
    Path.GetFileNameWithoutExtension(target) + "-readme.png");

using (FileStream output = File.Create(composite))
{
    PngBitmapEncoder encoder = new();
    encoder.Frames.Add(BitmapFrame.Create(Composite(image)));
    encoder.Save(output);
}

Console.WriteLine($"wrote {composite}");
return 0;

BitmapSource Composite(BitmapSource strip)
{
    const int Width = 1280;
    const int Gap = 14;
    const int DetailFrom = 0, DetailWidth = 640, DetailScale = 2;

    double wholeScale = Width / (double)strip.PixelWidth;
    int wholeHeight = (int)Math.Round(strip.PixelHeight * wholeScale);
    int detailHeight = strip.PixelHeight * DetailScale;

    DrawingVisual visual = new();

    using (DrawingContext dc = visual.RenderOpen())
    {
        dc.DrawRectangle(
            System.Windows.Media.Brushes.Black, null,
            new System.Windows.Rect(0, 0, Width, wholeHeight + Gap + detailHeight));

        dc.DrawImage(strip, new System.Windows.Rect(0, 0, Width, wholeHeight));

        // The leading section, magnified. RenderOptions is what keeps it per-pixel; the default
        // would resample and soften data that is meant to be read cell by cell.
        CroppedBitmap detail = new(strip,
            new System.Windows.Int32Rect(DetailFrom, 0, Math.Min(DetailWidth, strip.PixelWidth), strip.PixelHeight));

        RenderOptions.SetBitmapScalingMode(
            visual, BitmapScalingMode.NearestNeighbor);

        dc.DrawImage(detail, new System.Windows.Rect(
            0, wholeHeight + Gap, DetailWidth * DetailScale, detailHeight));
    }

    RenderTargetBitmap rendered = new(
        Width, wholeHeight + Gap + detailHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);

    rendered.Render(visual);
    rendered.Freeze();

    return rendered;
}

// A copy of Shell's HeatmapRenderer rather than a reference to it: that lives in the WPF
// application and pulling the whole shell in to colour some pixels would make this tool depend on
// ManagedShell. The ramp and the six-decade floor are the parts that must not drift, so they are
// stated here in the same terms.
BitmapSource Render(WearAccumulator wear)
{
    int width = wear.Width, height = wear.Height;
    double peak = wear.Peak;

    int stride = width * 4;
    byte[] pixels = new byte[stride * height];

    if (peak > 0.0)
    {
        ReadOnlySpan<double> cells = wear.Cells;

        for (int i = 0; i < cells.Length; i++)
        {
            double fraction = cells[i] / peak;

            if (fraction > 0.0)
            {
                const double floor = 1e-6;
                fraction = fraction < floor ? 0.0 : 1.0 - Math.Log10(fraction) / Math.Log10(floor);
            }

            (byte r, byte g, byte b) = Ramp(Math.Clamp(fraction, 0.0, 1.0));

            int p = i * 4;
            pixels[p + 0] = b;
            pixels[p + 1] = g;
            pixels[p + 2] = r;
            pixels[p + 3] = 255;
        }
    }

    return BitmapSource.Create(width, height, 96, 96,
        System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
}

static (byte, byte, byte) Ramp(double t)
{
    // Black, deep blue, magenta, orange, white — dark where nothing happened, so the image reads
    // as the bar does.
    (double At, byte R, byte G, byte B)[] stops =
    [
        (0.00, 0, 0, 0),
        (0.25, 20, 24, 90),
        (0.50, 150, 30, 120),
        (0.75, 240, 120, 40),
        (1.00, 255, 255, 235),
    ];

    for (int i = 1; i < stops.Length; i++)
    {
        if (t > stops[i].At && i < stops.Length - 1) continue;

        var a = stops[i - 1];
        var b = stops[i];
        double span = b.At - a.At;
        double k = span <= 0 ? 0 : Math.Clamp((t - a.At) / span, 0, 1);

        return ((byte)(a.R + (b.R - a.R) * k),
                (byte)(a.G + (b.G - a.G) * k),
                (byte)(a.B + (b.B - a.B) * k));
    }

    return (0, 0, 0);
}
