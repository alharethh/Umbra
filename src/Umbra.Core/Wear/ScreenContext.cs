using Umbra.Core.Color;

namespace Umbra.Core.Wear;

/// <summary>
/// Design-spec §2.4 — the bar's wear against the rest of the screen. This is the comparison that
/// decides whether the wear thesis is worth anything, and the one the README must state plainly.
/// </summary>
/// <remarks>
/// <para>
/// For a light-mode user the bar is 86–191× below the wear-limiting content on their screen, and
/// the whole design is an elaborate optimisation of a rounding error — the four-edge placement is
/// the real reason to build it. For a dark-mode user the bar's icons run ~5.8× <b>above</b> the
/// application surfaces around them, and unlike application content they never move, never scroll
/// and are present in every session.
/// </para>
/// <para>
/// These are arithmetic on assumed content luminances, not measurements (§9, "stated limits").
/// </para>
/// </remarks>
public static class ScreenContext
{
    /// <summary>A light-mode window body, <c>L = 0.80</c>.</summary>
    public const double LightWindowBody = 0.80;

    /// <summary>Mid-tone content, <c>L = 0.50</c>.</summary>
    public const double MidToneContent = 0.50;

    /// <summary>Dark-mode text and UI, <c>L = 0.05</c>.</summary>
    public const double DarkModeText = 0.05;

    /// <summary>A dark-mode application surface, <c>#1E1E1E</c>.</summary>
    public static double DarkModeSurface { get; } = Srgb.RelativeLuminance(Rgb.FromHex("#1E1E1E"));

    /// <summary>
    /// How many times the given screen content out-wears <paramref name="budget"/>. Values below 1
    /// mean the bar is the brighter of the two — which is the dark-mode case, and the point.
    /// </summary>
    public static double RatioTo(double contentLuminance, WearBudget budget,
                                 double exponent = WearModel.DefaultExponent) =>
        WearModel.Rate(contentLuminance, exponent) / budget.Total;
}
