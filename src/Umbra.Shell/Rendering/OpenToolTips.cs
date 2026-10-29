using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Finds and closes the tooltips this process has on screen.
/// </summary>
/// <remarks>
/// <para>
/// <c>ToolTipService</c> has no public way to close the tooltip it opened, but the tooltip itself
/// is an ordinary <see cref="ToolTip"/> near the root of its own popup window, and closing it there
/// is honoured: the service does not reopen it until the pointer enters something new.
/// </para>
/// <para>
/// A tooltip is a window of its own, so nothing done to the bar's content reaches it. That is why
/// the away blackout has to close them explicitly: the bar went black and the "Show desktop"
/// tooltip the pointer had been resting on stayed up at full brightness.
/// </para>
/// </remarks>
internal static class OpenToolTips
{
    /// <summary>How deep under a popup's root a tooltip is looked for.</summary>
    private const int SearchDepth = 4;

    /// <summary>Closes every open tooltip of this thread's, except <paramref name="except"/>.</summary>
    /// <returns>How many were closed, for the log.</returns>
    public static int CloseAll(ToolTip? except = null)
    {
        int closed = 0;

        // Copied first: closing a tooltip removes its source from the live collection.
        foreach (PresentationSource source in PresentationSource.CurrentSources.Cast<PresentationSource>().ToList())
        {
            if (source.CheckAccess() is false) continue;
            if (source.RootVisual is not Visual root) continue;
            if (Find(root, SearchDepth) is not { IsOpen: true } tip) continue;
            if (ReferenceEquals(tip, except)) continue;

            tip.IsOpen = false;
            closed++;
        }

        return closed;
    }

    private static ToolTip? Find(DependencyObject node, int depth)
    {
        if (node is ToolTip t) return t;
        if (depth == 0) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find(VisualTreeHelper.GetChild(node, i), depth - 1) is { } found) return found;
        return null;
    }
}
