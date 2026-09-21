#!/usr/bin/env dotnet run
// Wear measurement harness. Two jobs, both of which produced numbers in the README:
//
//   dotnet run tools/measure-wear.cs screen   what the bar is actually emitting, off the panel
//   dotnet run tools/measure-wear.cs drift    what drift does that a peak-duty table cannot see
//
// Traps this file exists to not rediscover:
//  - The process MUST be per-monitor-v2 DPI aware. A DPI-unaware capture is resampled by the
//    compositor and every luminance is wrong, quietly and plausibly.
//  - Wear is L^1.7 in LINEAR light. Accumulating sRGB values, or accumulating linearly in L,
//    under-ranks bright elements, which is backwards. See design-spec section 9.
//  - `screen` captures the whole primary display to compute statistics and writes no image
//    anywhere. Do not make it save one.
#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishTrimmed=false
#:property Nullable=disable

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

const double N = 1.7;                       // burn-in exponent, design-spec P2
static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
[DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
if (args.Length > 0 && args[0] == "drift") { Drift(); return; }
Screen();

// ---------------------------------------------------------------- screen
void Screen() {
    SetProcessDpiAwarenessContext(new IntPtr(-4));      // per-monitor v2, or the numbers are wrong
    int W = GetSystemMetrics(0), H = GetSystemMetrics(1);
    GetCursorPos(out var cur);
    int bar = (int)Math.Round(48 * (H > 1080 ? 1.25 : 1.0));   // reserved band, physical px

    // Precondition, and it is not optional. Umbra exiting mid-session is silent, and a capture of
    // Explorer's taskbar looks exactly like a capture of a badly-behaving Umbra: same rectangle,
    // plausible numbers, wrong subject. This cost one confusing reading already.
    bool up = System.Diagnostics.Process.GetProcesses()
                .Any(p => p.ProcessName.StartsWith("Umbra", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"physical {W}x{H}  bar={bar}px  cursor={cur.X},{cur.Y}  " +
                      $"{H - bar - cur.Y}px from the bar (>150 = Tier 0)");
    Console.WriteLine(up ? "SUBJECT: Umbra is running — the strip below is Umbra's bar."
                         : "SUBJECT: Umbra is NOT running — the strip below is EXPLORER'S taskbar.");

    using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(0, 0, 0, 0, new Size(W, H));
    var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var buf = new byte[d.Stride * H];
    Marshal.Copy(d.Scan0, buf, 0, buf.Length);
    int stride = d.Stride;
    bmp.UnlockBits(d);

    var lut = new double[256];
    for (int i = 0; i < 256; i++) lut[i] = Lin(i / 255.0);

    void Report(string name, int y0, int y1) {
        var a = new double[(y1 - y0) * W];
        int k = 0;
        for (int y = y0; y < y1; y++) {
            int o = y * stride;
            for (int x = 0; x < W; x++)
                a[k++] = 0.2126 * lut[buf[o + x * 4 + 2]]
                       + 0.7152 * lut[buf[o + x * 4 + 1]]
                       + 0.0722 * lut[buf[o + x * 4]];
        }
        var lit = a.Where(v => v > 0.002).ToArray();
        Console.WriteLine($"{name,-28} meanL={a.Average():F6}  wear/px={a.Average(v => Math.Pow(v, N)):F8}  " +
                          $"lit={lit.Length / (double)a.Length * 100,5:F2}%  meanL(lit)={(lit.Length > 0 ? lit.Average() : 0):F4}");
    }
    Report("the bar", H - bar, H);
    Report("200px above the bar", H - bar - 200, H - bar);
    Report("whole screen", 0, H);
}

// ---------------------------------------------------------------- drift
void Drift() {
    const double Ad = 5.46 * 1.25, Am = 1.95 * 1.25;   // design-spec 6.2, physical px at 125%
    const int S = 4, Half = 40;                        // sub-pixel sampling, profile half-width

    double[] Offsets(bool motion) {
        if (!motion) return [0.0];
        var o = new double[20000];
        for (int i = 0; i < o.Length; i++) {
            double t = i / (double)o.Length;
            double tri = 4 * Math.Abs(t - Math.Floor(t + 0.5)) - 1;          // constant velocity, no easing
            double mic = 0.5 * (Math.Sin(2 * Math.PI * t * 333)
                              + Math.Sin(2 * Math.PI * t * 333 * 60 / 83.0)); // the 60s/83s pair
            o[i] = Ad * tri + Am * mic;
        }
        return o;
    }
    double[] Profile(double w, double L, bool motion) {
        var offs = Offsets(motion);
        var acc = new double[Half * 2 * S];
        foreach (var off in offs)
            for (int i = 0; i < acc.Length; i++) {
                double x = (i - Half * S) / (double)S;
                double cov = Math.Clamp((w / 2 + 0.5 - Math.Abs(x - off)) / 1.0, 0, 1);
                acc[i] += Math.Pow(cov * L, N);
            }
        for (int i = 0; i < acc.Length; i++) acc[i] /= offs.Length;
        return acc;
    }

    Console.WriteLine($"{"feature",-36}{"px",6}{"static",11}{"drifting",11}{"less",9}");
    foreach (var (nm, w) in new[] { ("icon body, 16 dip dormant", 20.0), ("icon body, 24 dip", 30.0),
             ("running indicator pill", 3.75), ("clock / Start glyph stroke", 1.9),
             ("icon interior detail line", 2.5), ("anti-aliased glyph edge", 1.0) }) {
        double ps = Profile(w, 0.0513, false).Max(), pd = Profile(w, 0.0513, true).Max();
        Console.WriteLine($"{nm,-36}{w,6:F1}{ps,11:F5}{pd,11:F5}{ps / pd,8:F1}x");
    }

    var st = Profile(20, 0.0513, false);
    var dr = Profile(20, 0.0513, true);
    double Grad(double[] a) { double m = 0; for (int i = 1; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - a[i - 1]) * S); return m; }
    double Fall(double[] a) {
        double pk = a.Max();
        return (Array.FindIndex(a, v => v >= 0.9 * pk) - Array.FindIndex(a, v => v >= 0.1 * pk)) / (double)S;
    }
    Console.WriteLine($"\nboundary of a 20px icon — the part the peak-duty table cannot see:");
    Console.WriteLine($"  peak wear        {st.Max():F6} -> {dr.Max():F6}   ({st.Max() / dr.Max():F2}x)");
    Console.WriteLine($"  max gradient     {Grad(st):F6} -> {Grad(dr):F6}   ({Grad(st) / Grad(dr):F1}x softer)");
    Console.WriteLine($"  90->10 falloff   {Fall(st):F2}px -> {Fall(dr):F2}px");
    Console.WriteLine($"  energy conserved {dr.Sum() / st.Sum():F4}   (must be ~1.0 or the model is inventing light)");
}

struct POINT { public int X, Y; }
