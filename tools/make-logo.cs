#!/usr/bin/env dotnet run
// Renders Umbra's mark: logo.png and the nine-size umbra.ico.
//
//   dotnet run tools/make-logo.cs
//
// The mark is an eclipse: a black disc with a pale cyan corona on its rim. Its colour and
// proportions were measured off the approved artwork rather than guessed — ring #B0F7FB, stroke
// 2.05% of the canvas, glow reaching about 4% further out. Only the radius differs from that
// artwork, which placed the ring at 29.25% inside a square field; on a circular field the mark
// is drawn larger, because nothing is eating the margin any more.
//
// Two things here are deliberate and easy to "fix" wrongly:
//
//  - EVERY SIZE IS DRAWN, never downscaled. Resampling a 1024 px ring to 16 turns a 0.3 px
//    stroke into a grey smudge. The same reason the previous icon was built this way.
//  - THE FIELD IS AN OPAQUE BLACK DISC, and the ring sits ON ITS RIM. The disc is what keeps the
//    inside of the ring black on a light background; drop it and a pale Explorer window fills the
//    middle, turning an eclipse into a cyan donut on white. Putting the ring on the rim rather
//    than floating it inside a larger disc is what stops the mark reading as two concentric
//    circles, and it is what a real eclipse looks like: the occluding body, and its corona.
#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishTrimmed=false
#:property Nullable=disable

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Measured from the approved artwork at 1024 px.
var Ink = Color.FromArgb(0xB0, 0xF7, 0xFB);
const double TrueStrokeFraction = 0.0205;    // 21 px at 1024
const double TrueRadiusFraction = 0.390;     // the rim of the black disc, on a circular field
const double GlowFraction = 0.040;           // 41 px at 1024

string repo = Directory.GetCurrentDirectory();
string assets = Path.Combine(repo, "src", "Umbra.Shell", "Assets");
if (!Directory.Exists(assets)) { Console.Error.WriteLine($"no {assets} — run from the repo root"); return 1; }

// Windows asks for all of these; giving it every one stops it inventing its own.
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

Console.WriteLine($"{"size",5}{"radius",9}{"stroke",9}{"glow",8}");

var frames = new List<byte[]>();
foreach (int size in sizes)
{
    using Bitmap frame = Draw(size, out double r, out double w, out double g);
    Console.WriteLine($"{size,5}{r,9:F2}{w,9:F2}{g,8:F2}");

    using MemoryStream png = new();
    frame.Save(png, ImageFormat.Png);
    frames.Add(png.ToArray());
}

WriteIcon(Path.Combine(assets, "umbra.ico"), sizes, frames);
Console.WriteLine($"\nwrote umbra.ico ({sizes.Length} sizes, {new FileInfo(Path.Combine(assets, "umbra.ico")).Length / 1024} KB)");

using (Bitmap big = Draw(1024, out _, out _, out _))
    big.Save(Path.Combine(assets, "logo.png"), ImageFormat.Png);

Console.WriteLine("wrote logo.png (1024)");
return 0;

// ---------------------------------------------------------------- the mark

Bitmap Draw(int size, out double radius, out double stroke, out double glow)
{
    // <b>The stroke is not simply proportional, and it cannot be.</b> 2.05% of 16 px is a third
    // of a pixel: the ring would disappear. The fraction is interpolated geometrically against
    // log2(size) between a legible 12.5% at 16 px and the artwork's true 2.05% at 256, which
    // gives a smooth ramp rather than the dip a clamp produces in the middle sizes.
    double t = Math.Clamp((Math.Log2(size) - 4.0) / 4.0, 0.0, 1.0);
    double strokeFraction = 0.125 * Math.Pow(TrueStrokeFraction / 0.125, t);

    // The measured artwork put the ring at 29.25% inside a square field. On a circular field
    // there is no square eating the margin, so the mark is drawn larger — it has to hold its own
    // beside other applications' full-bleed icons — and grows a little further as it shrinks.
    double radiusFraction = 0.405 + (TrueRadiusFraction - 0.405) * t;

    radius = size * radiusFraction;
    stroke = size * strokeFraction;

    // No glow below 32 px — at that size it is a grey haze that eats the contrast the ring
    // needs, and the ring is doing all the work anyway.
    glow = size * GlowFraction * Math.Clamp((size - 16.0) / 32.0, 0.0, 1.0);

    Bitmap bitmap = new(size, size, PixelFormat.Format32bppArgb);

    using Graphics g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;

    g.Clear(Color.Transparent);

    float centre = size / 2f;
    RectangleF Circle(double r) => new(
        (float)(centre - r), (float)(centre - r), (float)(r * 2), (float)(r * 2));

    // The umbra itself: an opaque black disc out to the ring's centreline. The ring is drawn over
    // its edge afterwards, so the disc's own antialiased boundary never shows.
    using (SolidBrush shadow = new(Color.Black))
        g.FillEllipse(shadow, Circle(radius));

    // The corona, built from concentric strokes fading outward. A real Gaussian would be nicer
    // and is not worth a blur pass: at these radii the banding is below one 8-bit step.
    if (glow > 0.5)
    {
        const int Steps = 14;

        for (int i = Steps; i >= 1; i--)
        {
            double spread = glow * i / Steps;
            double falloff = Math.Exp(-3.0 * Math.Pow(i / (double)Steps, 2.0));
            int alpha = (int)Math.Round(58 * falloff);
            if (alpha <= 0) continue;

            using Pen halo = new(Color.FromArgb(alpha, Ink), (float)(stroke + spread * 2));
            g.DrawEllipse(halo, Circle(radius));
        }
    }

    using Pen pen = new(Ink, (float)stroke);
    g.DrawEllipse(pen, Circle(radius));

    return bitmap;
}

// ---------------------------------------------------------------- the .ico container

// PNG-compressed entries throughout, which Windows has read since Vista and which keeps the
// file a fifth of the size of the equivalent BMP entries with their AND masks.
void WriteIcon(string path, int[] sizes, List<byte[]> frames)
{
    using FileStream file = File.Create(path);
    using BinaryWriter w = new(file);

    w.Write((ushort)0);                 // reserved
    w.Write((ushort)1);                 // type: icon
    w.Write((ushort)sizes.Length);

    int offset = 6 + 16 * sizes.Length;

    for (int i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));   // 0 means 256
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0);               // palette size
        w.Write((byte)0);               // reserved
        w.Write((ushort)1);             // colour planes
        w.Write((ushort)32);            // bits per pixel
        w.Write(frames[i].Length);
        w.Write(offset);

        offset += frames[i].Length;
    }

    foreach (byte[] frame in frames) w.Write(frame);
}
