using System.Globalization;
using Umbra.Core.Color;
using Umbra.Core.Icons;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Rendering;

/// <summary>
/// The §5.1 step 6 diagnostic: what each icon actually costs.
/// </summary>
/// <remarks>
/// §5.1 is explicit that this ships as a diagnostic before it ships as a behaviour — "surface 'this
/// icon costs 4× the row median' in settings and let the user decide", because automatic dimming of
/// specific logos will look like a rendering bug. P90 normalisation equalises peak brightness, not
/// emitted energy, so two icons that look equally bright can differ several-fold in what they cost;
/// this is the number that shows it.
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

        double energy = IconNormalizer.IntegratedLuminance(art);

        BringUpLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"icon | {Truncate(title, 34),-34} | {art.Width}x{art.Height} " +
            $"| opaque {coverage,6:P1} | energy {energy,8:F2}"));
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "…";
}
