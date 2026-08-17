namespace Umbra.Core.Layout;

/// <summary>Which screen edge the bar occupies. Windows 11 supports bottom only; the other three are ours.</summary>
public enum BarEdge
{
    /// <summary>The shipping default (§7.1). Also the edge that contends with Explorer's autohidden taskbar.</summary>
    Bottom,

    /// <summary>Mirror of bottom. Start menu still opens at the bottom of the screen — accepted (§7.0).</summary>
    Top,

    /// <summary>No autohide contention, so the first edge to bring the bar up on (§11.1).</summary>
    Left,

    /// <summary>As left, mirrored.</summary>
    Right,
}

/// <summary>Where the icon row sits along the bar's long axis.</summary>
public enum Alignment
{
    /// <summary>
    /// The default (§7.1): left on horizontal bars, bottom on vertical ones. Left-alignment plus
    /// <c>TaskbarAl = 0</c> makes the Start menu open directly beneath our Start button.
    /// </summary>
    Start,

    /// <summary>Centred, as Windows 11 defaults to. A user setting.</summary>
    Center,
}

/// <summary>Edge helpers.</summary>
public static class BarEdgeExtensions
{
    /// <summary>True for <see cref="BarEdge.Left"/> and <see cref="BarEdge.Right"/>.</summary>
    public static bool IsVertical(this BarEdge edge) => edge is BarEdge.Left or BarEdge.Right;

    /// <summary>True for <see cref="BarEdge.Bottom"/> and <see cref="BarEdge.Top"/>.</summary>
    public static bool IsHorizontal(this BarEdge edge) => !edge.IsVertical();

    /// <summary>
    /// True where Explorer's autohidden taskbar shares our edge and its reveal contends with our
    /// Tier 2 trigger (§11.1). This is the shipping default, so the contention is a release
    /// blocker rather than something to design around.
    /// </summary>
    public static bool ContendsWithExplorerTaskbar(this BarEdge edge) => edge == BarEdge.Bottom;
}
