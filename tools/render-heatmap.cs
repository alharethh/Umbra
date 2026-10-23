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

// The colouring is Umbra.Core's HeatmapPixels, the same one the heatmap window and its saved
// picture use, so the README image and a user's own cannot drift apart.
BitmapSource Render(WearAccumulator wear) =>
    BitmapSource.Create(wear.Width, wear.Height, 96, 96,
        System.Windows.Media.PixelFormats.Bgra32, null, HeatmapPixels.Bgra(wear), wear.Width * 4);
