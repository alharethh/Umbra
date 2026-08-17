namespace Umbra.Core.Wear;

/// <summary>
/// Persistence for the wear accumulator (design-spec §9).
/// </summary>
/// <remarks>
/// <para>
/// The file holds the <b>raw accumulator</b> plus a monotonic wall-clock total, so the dataset
/// survives configuration changes: change the tier values or the bar's edge and the numbers
/// collected so far are still meaningful, because nothing here is normalised or pre-interpreted.
/// </para>
/// <para>
/// The exponent travels with the data too. A dataset collected at <c>n = 1.7</c> can be re-ranked
/// for a different exponent later, but only if the value it was collected at is known — hence
/// storing it rather than assuming it, and hence refusing to load a file into an accumulator
/// configured differently.
/// </para>
/// </remarks>
public static class WearStore
{
    /// <summary>"UMBRAWER" — identifies the file and catches a wrong path early.</summary>
    private const ulong Magic = 0x5245_5741_5242_4D55;

    /// <summary>Current format version.</summary>
    public const int Version = 1;

    /// <summary>Writes an accumulator to a stream.</summary>
    public static void Save(WearAccumulator accumulator, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(accumulator);
        ArgumentNullException.ThrowIfNull(stream);

        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(accumulator.Width);
        writer.Write(accumulator.Height);
        writer.Write(accumulator.Exponent);
        writer.Write(accumulator.ElapsedSeconds);

        foreach (double cell in accumulator.Cells) writer.Write(cell);
    }

    /// <summary>
    /// Reads an accumulator back. Returns <see langword="null"/> for a file that is not ours, is a
    /// version we do not understand, or is truncated — a corrupt dataset is worth losing, and it is
    /// never worth failing to start the bar over.
    /// </summary>
    public static WearAccumulator? Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            if (reader.ReadUInt64() != Magic) return null;
            if (reader.ReadInt32() != Version) return null;

            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            double exponent = reader.ReadDouble();
            double elapsed = reader.ReadDouble();

            if (width <= 0 || height <= 0 || exponent <= 0.0 || elapsed < 0.0) return null;

            long count = (long)width * height;
            if (count > 64_000_000) return null;               // a sane ceiling on a bar rectangle

            double[] cells = new double[count];
            for (long i = 0; i < count; i++) cells[i] = reader.ReadDouble();

            return WearAccumulator.Restore(width, height, exponent, elapsed, cells);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads an accumulator only if it matches the geometry and exponent now in use, so a dataset
    /// is never silently continued under different conditions from the ones that produced it.
    /// </summary>
    public static WearAccumulator? LoadCompatible(Stream stream, int width, int height, double exponent)
    {
        WearAccumulator? loaded = Load(stream);
        if (loaded is null) return null;

        bool matches = loaded.Width == width
                    && loaded.Height == height
                    && Math.Abs(loaded.Exponent - exponent) < 1e-9;

        return matches ? loaded : null;
    }
}
