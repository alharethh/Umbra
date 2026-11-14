namespace Umbra.Shell.Rendering;

/// <summary>
/// The user's resting brightness, as the fraction the tier converters and the icon pipeline pass
/// to <see cref="Umbra.Core.Tiers.TierPalette.Resolve"/>.
/// </summary>
/// <remarks>
/// Process-wide because the setting is: every bar of a per-monitor set draws with the same one,
/// and the converters are XAML resources with no other way in. Read on the UI thread only.
/// Changing it does not redraw anything by itself; the bar raises its drawn tier level after
/// setting it, which re-runs every tier binding.
/// </remarks>
internal static class RestingBrightness
{
    /// <summary>1 for the §3.1 table, down to <see cref="Umbra.Core.Tiers.TierPalette.MinRestingBrightness"/>.</summary>
    public static double Factor { get; set; } = 1.0;

    /// <summary>
    /// The factor quantised for cache keys: whole percent, which is what the setting holds.
    /// </summary>
    public static int Percent => (int)Math.Round(Factor * 100);

    /// <summary>
    /// The theme's hue for the monochrome greys, at their own luminance. Process-wide for the
    /// same reason as <see cref="Factor"/>.
    /// </summary>
    public static Umbra.Core.Color.RestingTint Tint
    {
        get => _tint;
        set
        {
            if (value == _tint) return;
            _tint = value;
            TintGeneration++;
        }
    }

    private static Umbra.Core.Color.RestingTint _tint = Umbra.Core.Color.RestingTint.None;

    /// <summary>
    /// Bumped whenever <see cref="Tint"/> changes, for cache keys: a bitmap or brush cached under
    /// one tint is the wrong colour under the next.
    /// </summary>
    public static int TintGeneration { get; private set; }

    /// <summary>The look the caches key on: brightness and tint together.</summary>
    public static int CacheKey => TintGeneration * 1000 + Percent;
}
