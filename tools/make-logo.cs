#!/usr/bin/env dotnet run
// Renders Umbra's mark: logo.png and the nine-size umbra.ico.
//
//   dotnet run tools/make-logo.cs
//
// The mark is an eclipse: a black disc with a pale cyan corona on its rim. Its colour and
// proportions were measured off the approved artwork rather than guessed — ring #B0F7FB, stroke
// 7.01% of the radius. The artwork placed that ring at 29.25% of a square canvas; on a circular
// field nothing eats the margin, so the mark is drawn larger and the stroke follows the radius
// rather than the canvas, which is what keeps it looking like the reference.
//
// Three things here are deliberate and easy to "fix" wrongly:
//
//  - EVERY SIZE IS DRAWN, never downscaled. Resampling a 1024 px ring to 16 turns a 0.3 px
//    stroke into a grey smudge. The same reason the previous icon was built this way.
//  - THE FIELD IS AN OPAQUE BLACK DISC, and the ring sits ON ITS RIM. The disc is what keeps the
//    inside of the ring black on a light background; drop it and a pale Explorer window fills the
//    middle, turning an eclipse into a cyan donut on white. Putting the ring on the rim rather
//    than floating it inside a larger disc is what stops the mark reading as two concentric
//    circles, and it is what a real eclipse looks like: the occluding body, and its corona.
//  - THE CORONA IS FAINT ON PURPOSE. Its strokes composite, so a generous alpha per step stacks
//    into a wide bright band that swallows the ring's edge and makes the mark look out of focus.
//    Reported exactly that way, and halving the alpha was most of the fix.
#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishTrimmed=false
#:property Nullable=disable

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Measured from the approved artwork at 1024 px.
var Ink = Color.FromArgb(0xB0, 0xF7, 0xFB);
const double StrokeOfRadius = 0.0701;        // 21 px on a 299.5 px radius, in the artwork
const double TrueRadiusFraction = 0.390;     // the rim of the black disc, on a circular field
const double GlowFraction = 0.024;           // a hint of bloom, not a haze — see Draw

string repo = Directory.GetCurrentDirectory();
string assets = Path.Combine(repo, "src", "Umbra.Shell", "Assets");
if (!Directory.Exists(assets)) { Console.Error.WriteLine($"no {assets} — run from the repo root"); return 1; }

// <b>Every size Windows asks for, and 96 is not optional.</b> Explorer's "Large icons" view wants
// 96 exactly; without a frame at that size Windows rescales the 128 one, and a resampled ring
// with a corona on it looks soft in the one view people judge an icon by. 72 and 80 are there for
// the intermediate DPI scalings that would otherwise resample too.
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 72, 80, 96, 128, 256];

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
    // <b>The stroke is not simply proportional, and it cannot be.</b> A third of a pixel at 16 px
    // and the ring disappears. The fraction is interpolated geometrically against log2(size)
    // between a legible 12.5% at 16 px and the artwork's proportion at 256, which ramps smoothly
    // where a clamp leaves the middle sizes thinner in proportion than the smallest.
    //
    // The large end is the artwork's stroke measured against its RADIUS (7.01%), not against the
    // canvas. The mark is drawn bigger on a circular field, so holding the canvas fraction
    // instead made the ring proportionally thinner than the reference — which, with the corona
    // over it, is what read as unsharp.
    double t = Math.Clamp((Math.Log2(size) - 4.0) / 4.0, 0.0, 1.0);
    double largeEnd = TrueRadiusFraction * StrokeOfRadius;
    double strokeFraction = 0.125 * Math.Pow(largeEnd / 0.125, t);

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

    // The corona, built from concentric strokes fading outward. Deliberately faint: these
    // strokes composite, so a generous alpha per step stacks into a wide bright band that
    // swallows the ring's edge and makes the whole mark look out of focus. It is a bloom around
    // a sharp ring, never a substitute for one.
    if (glow > 0.5)
    {
        const int Steps = 12;

        for (int i = Steps; i >= 1; i--)
        {
            double spread = glow * i / Steps;
            double falloff = Math.Exp(-4.5 * Math.Pow(i / (double)Steps, 2.0));
            int alpha = (int)Math.Round(26 * falloff);
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
