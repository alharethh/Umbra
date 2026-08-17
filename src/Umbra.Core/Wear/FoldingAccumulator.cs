namespace Umbra.Core.Wear;

/// <summary>
/// The memory-constrained accumulation strategy of design-spec §9: accumulate per-second
/// increments into a <see cref="float"/> buffer and fold that into a <see cref="double"/> total
/// once an hour.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that a float32 buffer is never the whole story. Naive float32 accumulation of
/// per-second wear is off by roughly 4% within two months and silently gets worse; folding hourly
/// keeps the buffer small enough that its rounding never compounds, and tracks the exact total to
/// about five significant figures indefinitely.
/// </para>
/// <para>
/// Prefer plain float64 (<see cref="WearAccumulator"/>). Reach for this only if the per-pixel
/// buffer genuinely cannot afford eight bytes a cell.
/// </para>
/// </remarks>
public sealed class FoldingAccumulator
{
    /// <summary>Fold interval from §9 — one hour of 1 Hz samples.</summary>
    public const double DefaultFoldIntervalSeconds = 3600.0;

    private double _total;
    private float _buffer;
    private double _sinceFold;

    /// <summary>Creates an accumulator that folds every <paramref name="foldIntervalSeconds"/>.</summary>
    public FoldingAccumulator(double foldIntervalSeconds = DefaultFoldIntervalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(foldIntervalSeconds);
        FoldIntervalSeconds = foldIntervalSeconds;
    }

    /// <summary>Seconds of accumulation between folds.</summary>
    public double FoldIntervalSeconds { get; }

    /// <summary>Number of folds performed — useful only for diagnostics.</summary>
    public int FoldCount { get; private set; }

    /// <summary>Accumulated total, including whatever is still sitting in the float32 buffer.</summary>
    public double Total => _total + _buffer;

    /// <summary>Accumulates <c>L^n · Δt</c> for one sample.</summary>
    public void Add(double luminance, double seconds, double exponent = WearModel.DefaultExponent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);

        _buffer += (float)WearModel.Accumulate(luminance, seconds, exponent);
        _sinceFold += seconds;
        if (_sinceFold >= FoldIntervalSeconds) Fold();
    }

    /// <summary>Folds the buffer into the float64 total. Called automatically; call on exit and on session lock.</summary>
    public void Fold()
    {
        _total += _buffer;
        _buffer = 0f;
        _sinceFold = 0.0;
        FoldCount++;
    }
}
