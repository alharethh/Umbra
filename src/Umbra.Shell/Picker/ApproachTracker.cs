using System.Windows;

namespace Umbra.Shell.Picker;

/// <summary>
/// Whether the pointer is on its way to a panel, by the safe-triangle rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>The diagonal problem.</b> A preview opens above its icon and is wider than it, so the
/// shortest path from the icon to a thumbnail off to one side runs diagonally — across the
/// neighbouring icons. Treating every icon the pointer touches as a new request switched the
/// preview to the neighbour's the instant the pointer crossed it, which is exactly when the user
/// was reaching for the one they had. Reported as "hovering File Explorer, it closes the preview
/// while I'm travelling to the windows, as soon as the cursor touches Discord".
/// </para>
/// <para>
/// The fix is the one Amazon's mega-menu made well known (Ben Kamens' <i>menu-aim</i>), now usually
/// called a safe triangle: take a point a moment back on the pointer's path and the far side of the
/// panel, and while the pointer stays inside the region between them it is heading for the panel and nothing it crosses on the way counts. Leave the region, or stop,
/// and the crossing counts again — so sliding along the bar to scrub through previews still switches
/// at once, which a plain delay would have ruined.
/// </para>
/// <para>
/// Free of timers and windows so the geometry can be tested. Every coordinate is in the same
/// screen space the caller supplies; the picker uses screen DIP throughout.
/// </para>
/// </remarks>
internal sealed class ApproachTracker
{
    /// <summary>
    /// How far back along the path the triangle's apex is taken from. Too short and two samples
    /// a few pixels apart give a direction that is mostly noise; too long and a change of mind
    /// takes that long to register.
    /// </summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromMilliseconds(90);

    /// <summary>Samples older than this say nothing about where the pointer is going now.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Slack around the panel, in DIP. A pointer aimed at a thumbnail's edge is aimed at the
    /// panel, and the hand is not that precise.
    /// </summary>
    public const double Tolerance = 10;

    /// <summary>
    /// Below this much movement over <see cref="StillWindow"/>, in DIP, the pointer is resting
    /// rather than travelling.
    /// </summary>
    public const double StillDistance = 3;

    /// <summary>
    /// How long a pointer must barely move to count as resting. <b>Longer than
    /// <see cref="LookBack"/> on purpose</b>: judging rest over the same 90 ms as direction read a
    /// slow, deliberate approach — about 20 DIP/s — as a stop, and released the switch the pointer
    /// was crawling past. Measured that way on hardware: the preview flipped between File Explorer
    /// and Discord every few hundred milliseconds.
    /// </summary>
    public static readonly TimeSpan StillWindow = TimeSpan.FromMilliseconds(250);

    private readonly List<(Point At, TimeSpan When)> _samples = [];

    /// <summary>Records where the pointer is.</summary>
    public void Add(Point at, TimeSpan when)
    {
        _samples.Add((at, when));
        _samples.RemoveAll(s => when - s.When > MaxAge);
    }

    /// <summary>Whether the pointer, now at <paramref name="cursor"/>, is heading into <paramref name="target"/>.</summary>
    public bool IsApproaching(Rect target, Point cursor, TimeSpan now)
    {
        Rect area = Inflate(target, Tolerance);
        if (area.Contains(cursor)) return true;

        Point? apex = ApexFor(now);
        if (apex is not { } from) return false;

        // Resting is not travelling. A pointer that has stopped on the neighbouring icon has
        // arrived there, and is asking for its preview.
        if (RestingAt(cursor, now)) return false;

        // No separate "getting closer" test is needed: distance to a convex region is convex, so no
        // point of the hull is further from the panel than the apex is. Inside the hull is closer.
        return InHull(from, area, cursor);
    }

    /// <summary>The oldest recent sample at least <see cref="LookBack"/> old, or the oldest of all.</summary>
    private Point? ApexFor(TimeSpan now)
    {
        (Point At, TimeSpan When)? apex = null;

        foreach ((Point at, TimeSpan when) in _samples)
        {
            if (now - when > MaxAge) continue;

            apex ??= (at, when);
            if (now - when >= LookBack) apex = (at, when);
        }

        return apex?.At;
    }

    /// <summary>Whether the pointer has stayed within <see cref="StillDistance"/> for the last <see cref="StillWindow"/>.</summary>
    private bool RestingAt(Point cursor, TimeSpan now)
    {
        Point? earliest = null;

        foreach ((Point at, TimeSpan when) in _samples)
        {
            if (now - when > StillWindow) continue;

            earliest ??= at;
            if ((cursor - at).Length >= StillDistance) return false;
        }

        return earliest is not null;
    }

    /// <summary>
    /// Whether <paramref name="p"/> lies in the convex hull of <paramref name="apex"/> and the
    /// rectangle — the rectangle plus the triangle the apex makes with each of its edges.
    /// </summary>
    private static bool InHull(Point apex, Rect rect, Point p)
    {
        Point a = rect.TopLeft, b = rect.TopRight, c = rect.BottomRight, d = rect.BottomLeft;

        return InTriangle(p, apex, a, b) || InTriangle(p, apex, b, c)
            || InTriangle(p, apex, c, d) || InTriangle(p, apex, d, a);
    }

    private static bool InTriangle(Point p, Point a, Point b, Point c)
    {
        double d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);

        bool negative = d1 < 0 || d2 < 0 || d3 < 0;
        bool positive = d1 > 0 || d2 > 0 || d3 > 0;

        return !(negative && positive);
    }

    private static double Cross(Point p, Point a, Point b) =>
        (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);

    private static Rect Inflate(Rect rect, double by)
    {
        rect.Inflate(by, by);
        return rect;
    }
}
