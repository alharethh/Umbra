using Umbra.Core.Wear;

namespace Umbra.Core.Tests;

/// <summary>Persistence for the wear accumulator (design-spec §9).</summary>
public class WearStoreTests
{
    [Fact]
    public void Round_Trips_Cells_Exactly()
    {
        // Not "to within a tolerance": the whole reason §9 specifies float64 is that small
        // per-second increments must not be lost, and a lossy save would reintroduce exactly the
        // failure the accumulator was designed to avoid.
        WearAccumulator original = new(7, 3);
        original.AddFrame(Frame(21, i => i / 100.0), 1.0);
        original.AddFrame(Frame(21, i => i / 250.0), 4.0);
        original.Idle(30.0);

        using MemoryStream stream = new();
        WearStore.Save(original, stream);
        stream.Position = 0;
        WearAccumulator? loaded = WearStore.Load(stream);

        Assert.NotNull(loaded);
        Assert.Equal(original.Width, loaded.Width);
        Assert.Equal(original.Height, loaded.Height);
        Assert.Equal(original.Exponent, loaded.Exponent);
        Assert.Equal(original.ElapsedSeconds, loaded.ElapsedSeconds);

        for (int i = 0; i < original.Cells.Length; i++)
            Assert.Equal(original.Cells[i], loaded.Cells[i]);
    }

    [Fact]
    public void Wall_Clock_Total_Survives_So_The_Dataset_Outlives_Config_Changes()
    {
        WearAccumulator original = new(4, 2);
        original.AddFrame(Frame(8, _ => 0.5), 60.0);
        original.Idle(3540.0);

        using MemoryStream stream = new();
        WearStore.Save(original, stream);
        stream.Position = 0;

        Assert.Equal(3600.0, WearStore.Load(stream)!.ElapsedSeconds);
    }

    [Fact]
    public void Exponent_Travels_With_The_Data()
    {
        // §9: store n alongside the data so the ranking can be recomputed for a different exponent
        // without re-collecting.
        WearAccumulator original = new(2, 2, exponent: 2.0);
        original.AddFrame(Frame(4, _ => 0.5), 1.0);

        using MemoryStream stream = new();
        WearStore.Save(original, stream);
        stream.Position = 0;

        Assert.Equal(2.0, WearStore.Load(stream)!.Exponent);
    }

    [Fact]
    public void Refuses_A_Dataset_Collected_Under_Different_Conditions()
    {
        WearAccumulator original = new(8, 4, exponent: 1.7);
        original.AddFrame(Frame(32, _ => 0.2), 1.0);

        using MemoryStream stream = new();
        WearStore.Save(original, stream);

        // Same geometry and exponent: continue the dataset.
        stream.Position = 0;
        Assert.NotNull(WearStore.LoadCompatible(stream, 8, 4, 1.7));

        // The bar moved edge, or DPI changed, or the exponent was retuned: start again rather than
        // silently blending incompatible data.
        stream.Position = 0;
        Assert.Null(WearStore.LoadCompatible(stream, 8, 5, 1.7));
        stream.Position = 0;
        Assert.Null(WearStore.LoadCompatible(stream, 16, 4, 1.7));
        stream.Position = 0;
        Assert.Null(WearStore.LoadCompatible(stream, 8, 4, 2.0));
    }

    [Fact]
    public void A_Corrupt_Or_Foreign_File_Returns_Null_Rather_Than_Throwing()
    {
        // Losing a wear dataset is acceptable. Failing to start the bar because of one is not.
        using MemoryStream garbage = new([1, 2, 3, 4, 5, 6, 7, 8, 9]);
        Assert.Null(WearStore.Load(garbage));

        using MemoryStream empty = new();
        Assert.Null(WearStore.Load(empty));
    }

    [Fact]
    public void A_Truncated_File_Returns_Null()
    {
        WearAccumulator original = new(16, 4);
        original.AddFrame(Frame(64, _ => 0.3), 1.0);

        using MemoryStream stream = new();
        WearStore.Save(original, stream);

        byte[] truncated = stream.ToArray()[..(int)(stream.Length / 2)];
        using MemoryStream partial = new(truncated);

        Assert.Null(WearStore.Load(partial));
    }

    [Fact]
    public void Restore_Rejects_A_Cell_Count_That_Does_Not_Match_The_Geometry()
    {
        Assert.Throws<ArgumentException>(() =>
            WearAccumulator.Restore(4, 4, 1.7, 0.0, new double[15]));
    }

    private static double[] Frame(int length, Func<int, double> luminance)
    {
        double[] frame = new double[length];
        for (int i = 0; i < length; i++) frame[i] = luminance(i);
        return frame;
    }
}
