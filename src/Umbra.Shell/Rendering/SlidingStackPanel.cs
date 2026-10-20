using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Umbra.Shell.Rendering;

/// <summary>
/// A stack panel whose children slide to their new positions instead of jumping to them.
/// </summary>
/// <remarks>
/// <para>
/// Reordering the icon row used to be instantaneous, which reads as a glitch rather than as a
/// move: nothing connects where an icon was to where it went, so the row appears to flicker into a
/// different arrangement. This arranges children exactly as a <see cref="StackPanel"/> would and
/// then puts the difference back as a <see cref="TranslateTransform"/>, animating it to zero. The
/// layout is never a frame out of date; only the paint lags, which is what makes it safe to leave
/// hit-testing alone — WPF hit-tests through a render transform, so a click during the slide lands
/// on the icon the user can see.
/// </para>
/// <para>
/// <b>The easing here is not a violation of §6.1's constant-velocity rule.</b> That rule is about
/// drift, where eased turnarounds concentrate residence time at the extremes and burn two hot
/// bands. This is a one-off 180 ms transition between two positions the user chose; it has no
/// turnarounds and no duty cycle, and it is the same treatment the hover chip and the Start press
/// already get.
/// </para>
/// <para>
/// Slides are per child and composed from the child's <i>current</i> visual position, so a reorder
/// that arrives while an earlier one is still in flight continues from where the icon actually is
/// rather than snapping to where it was going to be. That case is not exotic: it is every drag.
/// </para>
/// </remarks>
internal sealed class SlidingStackPanel : Panel
{
    /// <summary>How long an icon takes to reach its new slot.</summary>
    /// <remarks>
    /// Long enough to read as a move, short enough that a drag across the row does not feel like it
    /// is towing the icons behind it. Windows 11's own taskbar sits in the same region.
    /// </remarks>
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(180));

    /// <summary>Where the panel last arranged a child along the stacking axis.</summary>
    /// <remarks>
    /// Kept on the child rather than in a dictionary here so it cannot outlive it. A child with no
    /// recorded offset is new to the row and is placed rather than slid — an icon appearing for the
    /// first time should not fly in from the left edge.
    /// </remarks>
    private static readonly DependencyProperty ArrangedAtProperty = DependencyProperty.RegisterAttached(
        "ArrangedAt", typeof(double), typeof(SlidingStackPanel), new PropertyMetadata(double.NaN));

    /// <summary>Whether something else is positioning this child right now.</summary>
    private static readonly DependencyProperty HeldProperty = DependencyProperty.RegisterAttached(
        "Held", typeof(bool), typeof(SlidingStackPanel), new PropertyMetadata(false));

    /// <inheritdoc cref="StackPanel.Orientation"/>
    public static readonly DependencyProperty OrientationProperty =
        StackPanel.OrientationProperty.AddOwner(
            typeof(SlidingStackPanel),
            new FrameworkPropertyMetadata(
                Orientation.Horizontal,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Which way the row runs. Horizontal on a top or bottom bar, vertical down a side.</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>Whether the first child sits at the far end of the stacking axis.</summary>
    public static readonly DependencyProperty FromEndProperty = DependencyProperty.Register(
        nameof(FromEnd), typeof(bool), typeof(SlidingStackPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>
    /// Stacks the first child at the bottom (or right) and runs the rest back towards the top.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True on a vertical bar. Start sits in the bottom corner there (§7.3), and the row has to run
    /// <i>away</i> from it, as it runs away from Start along a bottom bar and as Windows 10's own
    /// vertical taskbar runs away from its Start at the top. A plain top-down stack reads the other
    /// way round: the first icon is the one furthest from Start, and a newly opened application
    /// lands beside Start instead of at the far end of the row.
    /// </para>
    /// <para>
    /// It also broke the rule that an icon holds its place. The row is bottom-aligned, so a top-down
    /// stack grows at its <i>bottom</i>: every application opening pushed every icon already there
    /// up by a slot. Run from the end, the row grows at the top and nothing already drawn moves.
    /// </para>
    /// <para>
    /// Positions recorded by <see cref="ArrangedAt"/> stay in the panel's own top-down coordinates
    /// either way, so holding, carrying and sliding need no case of their own. Only turning a point
    /// on the row into an index does, which is <see cref="SlotAt"/> and <see cref="GapAt"/>.
    /// </para>
    /// </remarks>
    public bool FromEnd
    {
        get => (bool)GetValue(FromEndProperty);
        set => SetValue(FromEndProperty, value);
    }

    private bool IsVertical => Orientation == Orientation.Vertical;

    /// <summary>
    /// Which slot a point <paramref name="along"/> the row falls in, clamped to the row.
    /// </summary>
    /// <param name="along">DIP from the panel's top (or left) edge, in its undrifted layout.</param>
    /// <param name="pitch">One slot's length along the row.</param>
    /// <param name="count">How many slots the row holds.</param>
    /// <param name="fromEnd">Whether the row runs from the far end, as <see cref="FromEnd"/>.</param>
    public static int SlotAt(double along, double pitch, int count, bool fromEnd)
    {
        if (count <= 0) return 0;

        double fromFirst = fromEnd ? count * pitch - along : along;
        return Math.Clamp((int)Math.Floor(fromFirst / pitch), 0, count - 1);
    }

    /// <summary>
    /// Which gap between slots a point <paramref name="along"/> the row is nearest: 0 before the
    /// first icon, <paramref name="count"/> after the last.
    /// </summary>
    /// <inheritdoc cref="SlotAt"/>
    public static int GapAt(double along, double pitch, int count, bool fromEnd)
    {
        double fromFirst = fromEnd ? count * pitch - along : along;
        return Math.Clamp((int)Math.Round(fromFirst / pitch), 0, Math.Max(0, count));
    }

    /// <summary>
    /// Takes a child out of the animation so a drag can carry it under the pointer.
    /// </summary>
    /// <remarks>
    /// A held child still gets arranged into whatever position the row has reordered it to. What it
    /// does not get is a slide towards it: the pointer, not the panel, says where it is drawn.
    /// </remarks>
    public void Hold(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);

        child.SetValue(HeldProperty, true);
        Offset(child).BeginAnimation(TranslateTransform.XProperty, null);
        Offset(child).BeginAnimation(TranslateTransform.YProperty, null);
    }

    /// <summary>Draws a held child <paramref name="along"/> DIP from where the layout put it.</summary>
    public void Carry(UIElement child, double along)
    {
        ArgumentNullException.ThrowIfNull(child);

        TranslateTransform offset = Offset(child);
        if (IsVertical) offset.Y = along;
        else offset.X = along;
    }

    /// <summary>Hands a held child back to the panel, which settles it into its slot.</summary>
    public void Release(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);

        child.SetValue(HeldProperty, false);
        Slide(child, Current(Offset(child)));
    }

    /// <summary>Where the panel last arranged a child, or NaN if it has not arranged it yet.</summary>
    public static double ArrangedAt(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);

        return (double)child.GetValue(ArrangedAtProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Unbounded along the stacking axis, as a StackPanel is: the row is as long as its icons,
        // and the bar sizes itself to it.
        Size unbounded = IsVertical
            ? new Size(availableSize.Width, double.PositiveInfinity)
            : new Size(double.PositiveInfinity, availableSize.Height);

        double along = 0;
        double across = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(unbounded);

            along += IsVertical ? child.DesiredSize.Height : child.DesiredSize.Width;
            across = Math.Max(across, IsVertical ? child.DesiredSize.Width : child.DesiredSize.Height);
        }

        return IsVertical ? new Size(across, along) : new Size(along, across);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double extent = IsVertical ? finalSize.Height : finalSize.Width;
        double along = 0;

        foreach (UIElement child in InternalChildren)
        {
            Size desired = child.DesiredSize;
            double length = IsVertical ? desired.Height : desired.Width;

            // From the end, the first child takes the last stretch of the panel and the rest stack
            // back towards the start. Measured against the arranged extent rather than the sum of
            // the children, so a panel given more room than it asked for still starts at the end.
            double at = FromEnd ? extent - along - length : along;

            child.Arrange(IsVertical
                ? new Rect(0, at, finalSize.Width, desired.Height)
                : new Rect(at, 0, desired.Width, finalSize.Height));

            Reposition(child, at);

            along += length;
        }

        return finalSize;
    }

    /// <summary>Reacts to a child having been arranged somewhere other than last time.</summary>
    private void Reposition(UIElement child, double target)
    {
        double previous = ArrangedAt(child);
        child.SetValue(ArrangedAtProperty, target);

        if (double.IsNaN(previous)) return;

        double shift = previous - target;
        if (Math.Abs(shift) < 0.5) return;

        if ((bool)child.GetValue(HeldProperty))
        {
            // The pointer owns this one. Absorb the layout jump so it does not flash a slot's width
            // sideways in the frame between the reorder and the next mouse move.
            Carry(child, Current(Offset(child)) + shift);
            return;
        }

        // Where the child is actually drawn, relative to where the layout has just put it. Reading
        // the transform rather than assuming zero is what keeps a second reorder mid-slide smooth.
        Slide(child, Current(Offset(child)) + shift);
    }

    /// <summary>Animates a child from <paramref name="from"/> DIP away back onto its slot.</summary>
    private void Slide(UIElement child, double from)
    {
        TranslateTransform offset = Offset(child);
        DependencyProperty axis = IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;

        if (Math.Abs(from) < 0.5)
        {
            offset.BeginAnimation(axis, null);
            offset.SetValue(axis, 0.0);
            return;
        }

        offset.BeginAnimation(axis, new DoubleAnimation(from, 0.0, SlideDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });

        // FillBehavior.Stop so the animation lets go of the property when it ends rather than
        // holding it at zero forever — a held property cannot be set, and the next drag sets it.
        offset.SetValue(axis, 0.0);
    }

    /// <summary>The child's own translate transform, created on first use.</summary>
    /// <remarks>
    /// One instance per child, never shared. Assigning a single <c>Transform</c> across many
    /// elements has already cost this project a day: it stopped every running indicator rendering,
    /// with correct widths and correct fills and nothing drawn.
    /// </remarks>
    private static TranslateTransform Offset(UIElement child)
    {
        if (child.RenderTransform is TranslateTransform existing) return existing;

        TranslateTransform created = new();
        child.RenderTransform = created;
        return created;
    }

    private double Current(TranslateTransform offset) => IsVertical ? offset.Y : offset.X;
}
