namespace Umbra.Core.Wear;

/// <summary>
/// Per-pixel wear accumulator over the reserved rectangle (design-spec §9). Phase 1 is
/// instrumentation only: accumulate, persist, draw a heatmap. No layout feedback.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cells are <see cref="double"/> and must stay that way.</b> §9 specifies float64 because
/// float32 drifts several percent within a couple of months of per-second accumulation and then
/// begins discarding increments outright, and the resulting heatmap still looks entirely plausible.
/// At 3840 × 56 the float64 buffer is 1680 KB, which is nothing. If memory ever does matter, use
/// <see cref="FoldingAccumulator"/> rather than demoting these cells.
/// </para>
/// <para>
/// Feed this from the known scene graph and transform — including the drift offset — not from a
/// GPU readback. It is cheaper, exact, and does not stall the pipeline once a second.
/// </para>
/// </remarks>
public sealed class WearAccumulator
{
    private readonly double[] _cells;

    /// <summary>Creates an accumulator over a <paramref name="width"/> × <paramref name="height"/> rectangle.</summary>
    public WearAccumulator(int width, int height, double exponent = WearModel.DefaultExponent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exponent);

        Width = width;
        Height = height;
        Exponent = exponent;
        _cells = new double[checked(width * height)];
    }

    /// <summary>
    /// Rebuilds an accumulator from persisted data. The cells are taken as-is, so
    /// <paramref name="cells"/> must have been produced at the same <paramref name="exponent"/>.
    /// </summary>
    public static WearAccumulator Restore(
        int width, int height, double exponent, double elapsedSeconds, ReadOnlySpan<double> cells)
    {
        WearAccumulator accumulator = new(width, height, exponent);

        if (cells.Length != accumulator._cells.Length)
            throw new ArgumentException(
                $"Expected {accumulator._cells.Length} cells for {width}×{height}, got {cells.Length}.",
                nameof(cells));

        ArgumentOutOfRangeException.ThrowIfNegative(elapsedSeconds);

        cells.CopyTo(accumulator._cells);
        accumulator.ElapsedSeconds = elapsedSeconds;
        return accumulator;
    }

    /// <summary>Width of the reserved rectangle, in pixels.</summary>
    public int Width { get; }

    /// <summary>Height of the reserved rectangle, in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// The wear exponent this dataset was collected with, stored alongside the data so the
    /// ranking can be recomputed for a different <c>n</c> without re-collecting (§9).
    /// </summary>
    public double Exponent { get; }

    /// <summary>
    /// Monotonic wall-clock seconds covered by this dataset, so it survives config changes.
    /// Advanced by <see cref="AddFrame"/> and <see cref="Idle"/>, not by <see cref="Add"/>.
    /// </summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>Bytes held by the cell buffer.</summary>
    public long SizeInBytes => (long)_cells.Length * sizeof(double);

    /// <summary>Accumulated wear at one cell.</summary>
    public double this[int x, int y] => _cells[Index(x, y)];

    /// <summary>The raw cells, row-major.</summary>
    public ReadOnlySpan<double> Cells => _cells;

    /// <summary>Total accumulated wear over the whole rectangle.</summary>
    public double Total
    {
        get
        {
            double sum = 0.0;
            foreach (double cell in _cells) sum += cell;
            return sum;
        }
    }

    /// <summary>The most-worn cell — the thing the heatmap exists to find.</summary>
    public double Peak
    {
        get
        {
            double peak = 0.0;
            foreach (double cell in _cells) if (cell > peak) peak = cell;
            return peak;
        }
    }

    /// <summary>Mean wear per cell.</summary>
    public double Mean => Total / _cells.Length;

    /// <summary>
    /// Accumulates <c>L^n · Δt</c> at one cell. Does not advance <see cref="ElapsedSeconds"/> —
    /// use <see cref="AddFrame"/> for a whole sample.
    /// </summary>
    public void Add(int x, int y, double luminance, double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        _cells[Index(x, y)] += WearModel.Accumulate(luminance, seconds, Exponent);
    }

    /// <summary>
    /// Accumulates one sample of the rendered scene: <paramref name="luminances"/> is the
    /// relative luminance of what was actually drawn at each cell, row-major, including the drift
    /// transform. Sample at 1 Hz (§9).
    /// </summary>
    public void AddFrame(ReadOnlySpan<double> luminances, double seconds)
    {
        if (luminances.Length != _cells.Length)
            throw new ArgumentException(
                $"Expected {_cells.Length} luminances for a {Width}×{Height} rectangle, got {luminances.Length}.",
                nameof(luminances));
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);

        for (int i = 0; i < luminances.Length; i++)
            _cells[i] += WearModel.Accumulate(luminances[i], seconds, Exponent);

        ElapsedSeconds += seconds;
    }

    /// <summary>
    /// Advances wall-clock time without accumulating — the bar drew nothing, or drew black.
    /// Keeps the denominator honest across sleep, lock and DPMS-off periods (§6.6).
    /// </summary>
    public void Idle(double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        ElapsedSeconds += seconds;
    }

    /// <summary>
    /// The heatmap: every cell as a fraction of <see cref="Peak"/>. All-zero input yields all zeroes.
    /// </summary>
    public double[] Normalised()
    {
        double peak = Peak;
        double[] result = new double[_cells.Length];
        if (peak <= 0.0) return result;
        for (int i = 0; i < _cells.Length; i++) result[i] = _cells[i] / peak;
        return result;
    }

    private int Index(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return y * Width + x;
    }
}
