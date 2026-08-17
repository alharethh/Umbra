namespace Umbra.Core.Tests;

/// <summary>
/// Assertions against the figures in <c>docs/verified-numbers.md</c>.
/// </summary>
/// <remarks>
/// Those figures were computed independently during design and are the authority: if an
/// implementation disagrees with one, the implementation is wrong. Tolerances here are therefore
/// set to the precision at which each figure is published — one unit in its last printed place —
/// rather than to whatever the code happens to produce. Several of the published budget totals
/// are truncated rather than rounded, which the one-unit allowance covers.
/// </remarks>
internal static class Approx
{
    /// <summary>Asserts <paramref name="actual"/> matches a documented figure within <paramref name="tolerance"/>.</summary>
    public static void Equal(double documented, double actual, double tolerance, string what)
    {
        double delta = actual - documented;
        Assert.True(
            Math.Abs(delta) <= tolerance,
            $"{what}: documented {documented:G10}, computed {actual:G10} (Δ {delta:E3}, tolerance ±{tolerance:G4}).");
    }

    /// <summary>Asserts a relative match, for figures whose absolute scale is not the point.</summary>
    public static void Relative(double documented, double actual, double relativeTolerance, string what)
    {
        double relative = documented == 0.0
            ? Math.Abs(actual)
            : Math.Abs((actual - documented) / documented);

        Assert.True(
            relative <= relativeTolerance,
            $"{what}: documented {documented:G10}, computed {actual:G10} " +
            $"(relative {relative:E3}, tolerance {relativeTolerance:G4}).");
    }
}
