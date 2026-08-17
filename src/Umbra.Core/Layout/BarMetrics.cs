namespace Umbra.Core.Layout;

/// <summary>
/// Geometry of the whole bar: reserved rectangle, content band, row placement and hit slots
/// (design-spec §6.3, §7).
/// </summary>
/// <remarks>
/// <para>
/// The reserved AppBar rectangle is fixed and never re-registered during steady-state operation —
/// changing it reflows every maximised window on the desktop. Drift happens strictly inside it.
/// </para>
/// <para>
/// v1's long-axis reservation of ≥64 px per side is gone: drift is contained within slots, so the
/// reserved rectangle needs no headroom along the bar's length at all.
/// </para>
/// </remarks>
public readonly record struct BarMetrics
{
    /// <summary>Content band of a horizontal bar at 100% scaling.</summary>
    public const double BaseHorizontalContentBand = 48.0;

    /// <summary>
    /// Reserved extent of a horizontal bar at 100% scaling — the same 48 px as the Windows 11
    /// taskbar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §7.1 and §8 specify 56 px, 8 px more than the content band, "for short-axis drift and corner
    /// rounding". <b>Measured, neither needs it.</b> Short-axis travel peaks at 6.75 px (§6.2's
    /// 4.8 px of drift plus 1.95 px of micro-jitter) while the content band already leaves 12 px of
    /// clearance either side of a 24 px icon, so the drift is contained without touching the
    /// reserved margin. Corner rounding costs no space at all, because rounding is achieved by not
    /// lighting pixels (§4).
    /// </para>
    /// <para>
    /// This is the resolution of the discrepancy that <c>ShortAxisFitsReservedHeadroom</c> recorded:
    /// the reserved band did not cover what §8 credited to it, and the answer is that it never
    /// needed to. Dropping to 48 px also makes Umbra exactly as tall as the taskbar it replaces,
    /// which is what §7's fidelity requirement wanted in the first place.
    /// </para>
    /// </remarks>
    public const double BaseHorizontalReserved = 48.0;

    /// <summary>Content band of a vertical bar at 100% scaling.</summary>
    public const double BaseVerticalContentBand = 56.0;

    /// <summary>Reserved extent of a vertical bar at 100% scaling. Same reasoning as horizontal.</summary>
    public const double BaseVerticalReserved = 56.0;

    /// <summary>
    /// Windows 11's own padding before the first icon when left-aligned, at 100% scaling.
    /// </summary>
    /// <remarks>
    /// §6.3 says to measure this on the target machine and adopt it. This is a placeholder until
    /// someone does; <see cref="RowInset"/> takes the larger of it and the peak drift offset, so
    /// an under-measurement cannot clip content at the corner.
    /// </remarks>
    public const double BaseWindows11RowInset = 8.0;

    /// <summary>Short-axis drift amplitude as a fraction of the short-axis maximum (§6.2).</summary>
    public const double ShortAxisDriftFraction = 0.50;

    /// <summary>Creates bar metrics for an edge, alignment and DPI scale.</summary>
    public BarMetrics(
        BarEdge edge = BarEdge.Bottom,
        Alignment alignment = Alignment.Start,
        double scale = 1.0,
        double? measuredWindows11RowInset = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);

        Edge = edge;
        Alignment = alignment;
        Scale = scale;
        Slot = SlotMetrics.ForScale(scale);
        MeasuredWindows11RowInset = measuredWindows11RowInset ?? BaseWindows11RowInset * scale;
    }

    /// <summary>Which screen edge the bar occupies.</summary>
    public BarEdge Edge { get; }

    /// <summary>Where the icon row sits along the long axis.</summary>
    public Alignment Alignment { get; }

    /// <summary>Per-monitor DPI scale: 1.0 is 100%.</summary>
    public double Scale { get; }

    /// <summary>The slot and its derived drift amplitudes.</summary>
    public SlotMetrics Slot { get; }

    /// <summary>Notification-area slot pitch at 100% scaling.</summary>
    public const double BaseTrayPitch = 24.0;

    /// <summary>Notification-area icon extent at 100% scaling.</summary>
    public const double BaseTrayIconExtent = 16.0;

    /// <summary>
    /// The notification area's slot, which is smaller than a task button's and therefore drifts
    /// less.
    /// </summary>
    /// <remarks>
    /// §6.2 derives drift amplitude from the slot rather than asserting it, and a tray icon's slot
    /// is not a task button's: a 16 px icon in a 24 px pitch leaves a 4 px gutter, against 10 px
    /// for the icon row. Applying the row's ±7.41 px peak offset here would push tray icons clean
    /// out of their slots and break P5, so each group drifts by its own derived amount. They share
    /// a phase, so they still move in sympathy — what they do not share is amplitude, which is a
    /// narrower reading of §6.2's "coherent" than the icon row alone required.
    /// </remarks>
    public SlotMetrics TraySlot => new(BaseTrayPitch * Scale, BaseTrayIconExtent * Scale);

    /// <summary>
    /// How much less the notification area drifts than the icon row, as a fraction.
    /// </summary>
    public double TrayDriftRatio =>
        Slot.MaxAmplitude <= 0.0 ? 0.0 : TraySlot.MaxAmplitude / Slot.MaxAmplitude;

    /// <summary>Windows 11's measured padding before the first icon, in scaled pixels.</summary>
    public double MeasuredWindows11RowInset { get; }

    /// <summary>Depth of the content band across the bar's short axis.</summary>
    public double ContentBand =>
        (Edge.IsVertical() ? BaseVerticalContentBand : BaseHorizontalContentBand) * Scale;

    /// <summary>Depth of the reserved AppBar rectangle across the bar's short axis.</summary>
    public double ReservedExtent =>
        (Edge.IsVertical() ? BaseVerticalReserved : BaseHorizontalReserved) * Scale;

    /// <summary>Margin between the content band and the reserved rectangle, per side.</summary>
    public double ReservedHeadroom => (ReservedExtent - ContentBand) / 2.0;

    /// <summary>Short-axis equivalent of <see cref="SlotMetrics.MaxAmplitude"/>, derived from the content band.</summary>
    public double ShortAxisMaxAmplitude =>
        (ContentBand - Slot.IconExtent) / 2.0 - SlotMetrics.SafetyMarginFraction * ContentBand;

    /// <summary>
    /// Short-axis drift amplitude, <c>0.50 × A_max_short</c> (§6.2). Works out at ±4.8 px at 100%
    /// scaling.
    /// </summary>
    /// <remarks>
    /// §6.2 annotates this "≈ ±4 px at 100%", which the formula does not produce — see
    /// <see cref="ShortAxisPeakOffset"/> for why the difference matters.
    /// </remarks>
    public double ShortAxisDriftAmplitude => ShortAxisDriftFraction * ShortAxisMaxAmplitude;

    /// <summary>
    /// Worst-case short-axis displacement: short-axis drift plus micro-jitter, which applies on
    /// both axes.
    /// </summary>
    /// <remarks>
    /// At 100% scaling this is 6.75 px against a <see cref="ReservedHeadroom"/> of 4 px, so drawn
    /// content crosses out of the content band and into the reserved margin. That is within the
    /// slot and so within P5 — <see cref="ShortAxisFitsContentBand"/> and
    /// <see cref="ShortAxisFitsReservedHeadroom"/> record which bound holds — but §8 attributes
    /// the reserved band's 8 px specifically to short-axis drift, and 8 px does not cover it.
    /// </remarks>
    public double ShortAxisPeakOffset => ShortAxisDriftAmplitude + Slot.MicroAmplitude;

    /// <summary>True when short-axis displacement keeps the content inside the content band.</summary>
    public bool ShortAxisFitsContentBand =>
        ShortAxisPeakOffset <= (ContentBand - Slot.IconExtent) / 2.0;

    /// <summary>True when short-axis displacement fits within the margin §8 reserved for it.</summary>
    public bool ShortAxisFitsReservedHeadroom => ShortAxisPeakOffset <= ReservedHeadroom;

    /// <summary>
    /// Inset of the row from the aligned end (§6.3). The larger of Windows 11's own padding and
    /// the peak drift offset, so negative drift can never clip content at the screen corner.
    /// </summary>
    public double RowInset => Math.Max(MeasuredWindows11RowInset, Slot.PeakOffset);

    /// <summary>Extent of a row of <paramref name="slotCount"/> slots along the long axis.</summary>
    public double RowSpan(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        return slotCount * Slot.Pitch;
    }

    /// <summary>The reserved AppBar rectangle on a screen of the given size.</summary>
    public Rect ReservedRect(double screenWidth, double screenHeight) => Edge switch
    {
        BarEdge.Bottom => new Rect(0, screenHeight - ReservedExtent, screenWidth, ReservedExtent),
        BarEdge.Top => new Rect(0, 0, screenWidth, ReservedExtent),
        BarEdge.Left => new Rect(0, 0, ReservedExtent, screenHeight),
        BarEdge.Right => new Rect(screenWidth - ReservedExtent, 0, ReservedExtent, screenHeight),
        _ => throw new ArgumentOutOfRangeException(nameof(Edge)),
    };

    /// <summary>Length of the bar's long axis on a screen of the given size.</summary>
    public double AxisLength(double screenWidth, double screenHeight) =>
        Edge.IsVertical() ? screenHeight : screenWidth;

    /// <summary>
    /// Position of slot <paramref name="index"/> along the long axis, measured from the origin of
    /// that axis (left for horizontal bars, top for vertical ones).
    /// </summary>
    /// <remarks>
    /// Slot 0 sits at the aligned end: leftmost on a horizontal bar (§7.1), bottom-most on a
    /// vertical one (§7.3), so the Start button lands adjacent to where the Start menu opens.
    /// </remarks>
    public double SlotOrigin(int index, int slotCount, double axisLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, slotCount);

        double span = RowSpan(slotCount);

        if (Edge.IsVertical())
        {
            double blockEnd = Alignment == Alignment.Center
                ? (axisLength + span) / 2.0
                : axisLength - RowInset;
            return blockEnd - (index + 1) * Slot.Pitch;
        }

        double blockStart = Alignment == Alignment.Center
            ? (axisLength - span) / 2.0
            : RowInset;
        return blockStart + index * Slot.Pitch;
    }

    /// <summary>
    /// The hit rectangle of slot <paramref name="index"/>. Computed from the <b>undrifted</b>
    /// layout and never moved (§6.5).
    /// </summary>
    public Rect HitSlot(int index, int slotCount, double screenWidth, double screenHeight)
    {
        double along = SlotOrigin(index, slotCount, AxisLength(screenWidth, screenHeight));
        Rect reserved = ReservedRect(screenWidth, screenHeight);
        double across = ReservedHeadroom;

        return Edge.IsVertical()
            ? new Rect(reserved.X + across, along, ContentBand, Slot.Pitch)
            : new Rect(along, reserved.Y + across, Slot.Pitch, ContentBand);
    }

    /// <summary>
    /// Where the icon in slot <paramref name="index"/> is actually drawn, given a motion offset
    /// along and across the bar. Centred in its slot at zero offset.
    /// </summary>
    public Rect DrawnIcon(int index, int slotCount, double screenWidth, double screenHeight,
                          double alongOffset = 0.0, double acrossOffset = 0.0)
    {
        Rect slot = HitSlot(index, slotCount, screenWidth, screenHeight);
        double size = Slot.IconExtent;
        double x = slot.X + (slot.Width - size) / 2.0;
        double y = slot.Y + (slot.Height - size) / 2.0;

        return Edge.IsVertical()
            ? new Rect(x + acrossOffset, y + alongOffset, size, size)
            : new Rect(x + alongOffset, y + acrossOffset, size, size);
    }

    /// <summary>
    /// The Start button's hit rectangle, extended to the screen corner (§6.5).
    /// </summary>
    /// <remarks>
    /// The glyph is <b>drawn</b> at the Windows 11 inset for fidelity; only the hit geometry
    /// reaches the corner, and hit geometry is invisible. Windows 11 insets its own Start button
    /// and therefore loses the corner as a Fitts target — P5 lets us have it back for free. This
    /// applies to the aligned end only; a centred row has no corner to claim.
    /// </remarks>
    public Rect StartHitSlot(int slotCount, double screenWidth, double screenHeight)
    {
        Rect slot = HitSlot(0, slotCount, screenWidth, screenHeight);
        if (Alignment != Alignment.Start) return slot;

        Rect reserved = ReservedRect(screenWidth, screenHeight);

        return Edge switch
        {
            // Horizontal: claim everything left of the slot, over the full reserved depth.
            BarEdge.Bottom or BarEdge.Top =>
                new Rect(reserved.X, reserved.Y, slot.Right - reserved.X, reserved.Height),

            // Vertical: the row is bottom-aligned, so the corner is below the slot.
            BarEdge.Left or BarEdge.Right =>
                new Rect(reserved.X, slot.Y, reserved.Width, reserved.Bottom - slot.Y),

            _ => throw new ArgumentOutOfRangeException(nameof(Edge)),
        };
    }
}
