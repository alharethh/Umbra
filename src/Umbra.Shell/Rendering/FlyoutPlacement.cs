using System.Windows;
using System.Windows.Controls.Primitives;
using Umbra.Core.Layout;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Where a flyout anchored to a control on the bar goes: centred on that control, on the inboard
/// side of whichever edge the bar occupies.
/// </summary>
/// <remarks>
/// <para>
/// <b>WPF's own <see cref="PlacementMode"/> values cannot express this.</b>
/// <see cref="PlacementMode.Top"/> aligns the popup's <i>left</i> edge with the target's left edge
/// and lifts it by the popup's height. The overflow chevron sits near the trailing end of the bar,
/// so a five-column panel hung off its left edge runs past the end of the screen, and Windows
/// shoves it back inboard to keep it visible — which is why the reported symptom was "opens to the
/// right of my screen" rather than anything that looked like an alignment bug.
/// </para>
/// <para>
/// The placement is computed rather than offset after the fact because a
/// <see cref="CustomPopupPlacementCallback"/> is handed the popup's measured size before it is
/// shown. Setting <c>HorizontalOffset</c> once the popup is open would work too, but only after a
/// layout pass has produced a width — so the panel would be placed wrongly for one frame, every
/// time.
/// </para>
/// <para>
/// Returned as the offset of the popup's top-left corner from the target's top-left corner, in
/// device-independent pixels, which is the coordinate system the callback works in.
/// </para>
/// </remarks>
internal static class FlyoutPlacement
{
    /// <summary>
    /// Clearance between the flyout and the control it belongs to.
    /// </summary>
    /// <remarks>
    /// Small on purpose. The pointer has to cross this gap to reach the flyout, and the watcher
    /// that closes the flyout when the pointer leaves treats the journey as "away" — it survives
    /// on a grace period, which a wider gap would eat into.
    /// </remarks>
    internal const double Gap = 6.0;

    /// <summary>
    /// The flyout's top-left corner, relative to the target's, for a bar on <paramref name="edge"/>.
    /// </summary>
    public static Point Centred(BarEdge edge, Size popup, Size target, double gap = Gap) => edge switch
    {
        // Above the bar, centred along it.
        BarEdge.Bottom => new Point(Centre(target.Width, popup.Width), -(popup.Height + gap)),

        // Below it.
        BarEdge.Top => new Point(Centre(target.Width, popup.Width), target.Height + gap),

        // Inboard of a vertical bar, centred on the other axis.
        BarEdge.Left => new Point(target.Width + gap, Centre(target.Height, popup.Height)),
        BarEdge.Right => new Point(-(popup.Width + gap), Centre(target.Height, popup.Height)),

        _ => new Point(Centre(target.Width, popup.Width), -(popup.Height + gap)),
    };

    /// <summary>
    /// The axis the flyout may slide along when it does not fit on screen.
    /// </summary>
    /// <remarks>
    /// Always the bar's long axis. Sliding that way keeps the flyout against the bar and merely
    /// off-centre, which is what Windows does with its own overflow panel near a screen corner.
    /// Letting WPF nudge it on the <i>short</i> axis instead would lift it off the bar, leaving a
    /// gap the pointer has to cross to reach it — and the pointer watcher closes the flyout when
    /// the pointer is over neither.
    /// </remarks>
    public static PopupPrimaryAxis SlideAxis(BarEdge edge) =>
        edge.IsVertical() ? PopupPrimaryAxis.Vertical : PopupPrimaryAxis.Horizontal;

    private static double Centre(double target, double popup) => (target - popup) / 2.0;
}
