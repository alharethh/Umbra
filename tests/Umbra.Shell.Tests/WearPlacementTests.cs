using System.IO;
using Umbra.Core.Layout;
using Umbra.Shell.Wear;

namespace Umbra.Shell.Tests;

/// <summary>
/// Moving the bar must not cost the wear history of the placement it left (backlog F2b).
/// </summary>
/// <remarks>
/// The accumulator is sized to the reserved rectangle in physical pixels, and
/// <c>WearStore.LoadCompatible</c> refuses a grid of another shape, deliberately. With one file
/// for every placement, the new grid's sampler refused the old record and then saved over it, so
/// moving back started from nothing. The heatmap is the lead picture; a user who tries another
/// edge for an afternoon must not lose days of it.
/// </remarks>
public class WearPlacementTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"umbra-wear-{Guid.NewGuid():N}");

    public WearPlacementTests() => Directory.CreateDirectory(_folder);

    /// <summary>The default placement keeps the plain file, so existing history carries on.</summary>
    [Fact]
    public void The_Bottom_Edge_Keeps_The_Screens_Own_File()
    {
        Assert.Equal(WearSampler.DefaultPath, WearSampler.PathFor(null, primary: true, BarEdge.Bottom));
        Assert.Equal(
            WearSampler.PathForScreen(@"\\.\DISPLAY2", primary: false),
            WearSampler.PathFor(@"\\.\DISPLAY2", primary: false, BarEdge.Bottom));
    }

    /// <summary>
    /// Every edge of every screen is its own record, including top and bottom, which are the same
    /// size: keyed by size they would share a file, and the top bar's wear would be written into
    /// the bottom bar's pixels.
    /// </summary>
    [Fact]
    public void Every_Placement_Has_Its_Own_File()
    {
        List<string> paths = [];

        foreach (string? device in new[] { null, @"\\.\DISPLAY2" })
        foreach (BarEdge edge in Enum.GetValues<BarEdge>())
            paths.Add(WearSampler.PathFor(device, primary: device is null, edge));

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (string path in paths)
        {
            Assert.Equal(Path.GetDirectoryName(WearSampler.DefaultPath), Path.GetDirectoryName(path));
            Assert.Equal(-1, Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()));
            Assert.EndsWith(".dat", path);
        }
    }

    /// <summary>
    /// Bottom to top on one screen is the same grid size and still a move. The old check compared
    /// the size alone, kept the bottom bar's sampler, and recorded the top bar into it.
    /// </summary>
    [Fact]
    public void A_Same_Size_Move_Still_Gets_A_New_Sampler()
    {
        string bottom = WearSampler.PathFor(null, true, BarEdge.Bottom);
        string top = WearSampler.PathFor(null, true, BarEdge.Top);

        Assert.True(WearSampler.NeedsNewSampler(bottom, 2256, 72, top, 2256, 72));
        Assert.True(WearSampler.NeedsNewSampler(bottom, 2256, 72, bottom, 84, 1504));
        Assert.True(WearSampler.NeedsNewSampler(null, 0, 0, bottom, 2256, 72));

        // A re-layout at the same placement and size keeps the running sampler.
        Assert.False(WearSampler.NeedsNewSampler(bottom, 2256, 72, bottom, 2256, 72));
    }

    /// <summary>
    /// The whole round trip with real samplers and real files: record at the bottom, move left,
    /// record there, move back, and the bottom record resumes with its hours intact; then the left
    /// record is still there too.
    /// </summary>
    [Fact]
    public void Moving_Away_And_Back_Resumes_The_Earlier_Record()
    {
        string bottom = Here(WearSampler.PathFor(null, true, BarEdge.Bottom));
        string left = Here(WearSampler.PathFor(null, true, BarEdge.Left));

        using (WearSampler first = Sampler(2256, 72, bottom))
            first.Accumulator.AddFrame(Lit(2256, 72), 3600);

        using (WearSampler moved = Sampler(84, 1504, left))
        {
            Assert.Equal(0, moved.Accumulator.ElapsedSeconds);
            moved.Accumulator.AddFrame(Lit(84, 1504), 600);
        }

        using (WearSampler back = Sampler(2256, 72, bottom))
        {
            Assert.Equal(3600, back.Accumulator.ElapsedSeconds, 6);
            Assert.True(back.Accumulator.Total > 0);
        }

        using (WearSampler again = Sampler(84, 1504, left))
            Assert.Equal(600, again.Accumulator.ElapsedSeconds, 6);
    }

    private string Here(string path) => Path.Combine(_folder, Path.GetFileName(path));

    private static WearSampler Sampler(int width, int height, string path) =>
        new(width, height, () => [], () => 1.0, path);

    private static double[] Lit(int width, int height)
    {
        double[] frame = new double[width * height];
        Array.Fill(frame, 0.01);
        return frame;
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
