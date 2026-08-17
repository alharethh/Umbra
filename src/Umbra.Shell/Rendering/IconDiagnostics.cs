using System.Globalization;
using Umbra.Core.Color;
using Umbra.Core.Icons;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Rendering;

/// <summary>
/// The §5.1 step 6 diagnostic: what each icon actually costs, and what outline mode would save on it.
/// </summary>
/// <remarks>
/// <para>
/// §5.1 is explicit that this ships as a diagnostic before it ships as a behaviour — "surface 'this
/// icon costs 4× the row median' in settings and let the user decide", because automatic dimming of
/// specific logos will look like a rendering bug.
/// </para>
/// <para>
/// It also answers a question the spec assumes rather than checks. §5.3 justifies outline mode for
/// "pin sets dominated by large solid-fill logos", but modern application icons are largely not
/// that — they are small shapes on transparent backgrounds, so the interior there is to reclaim may
/// be far smaller than the 70–85% figure suggests. Measuring a real pin set is the only way to know
/// whether the mode earns its place, and that is the same argument §11.2 Q1 makes about drift.
/// </para>
/// </remarks>
internal static class IconDiagnostics
{
    private static readonly HashSet<string> Reported = [];

    /// <summary>Reports an icon once, the first time it is seen.</summary>
    public static void Report(string title, IconBitmap art)
    {
        if (string.IsNullOrWhiteSpace(title)) title = "(untitled)";
        if (!Reported.Add(title)) return;

        double opaque = 0;
        foreach (Rgba pixel in art.Pixels) if (pixel.A > 0) opaque++;

        double coverage = art.Length > 0 ? opaque / art.Length : 0.0;

        double filled = IconNormalizer.IntegratedLuminance(art);
        double outlined = IconNormalizer.IntegratedLuminance(IconNormalizer.ToOutline(art));
        double saving = filled > 0.0 ? 1.0 - outlined / filled : 0.0;

        BringUpLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"icon | {Truncate(title, 34),-34} | {art.Width}x{art.Height} " +
            $"| opaque {coverage,6:P1} | energy {filled,8:F2} | outline saves {saving,6:P1}"));
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "…";
}
