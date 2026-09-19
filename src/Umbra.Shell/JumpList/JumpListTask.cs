namespace Umbra.Shell.JumpList;

/// <summary>
/// One entry from an application's jump list — "Open new tab", "New private window".
/// </summary>
/// <param name="Title">What the menu shows. From <c>System.Title</c>, which is what the app set.</param>
/// <param name="Path">The target. Usually the application's own executable.</param>
/// <param name="Arguments">What distinguishes this task from simply launching the application.</param>
/// <param name="IconPath">Where the icon lives, or empty to fall back to the application's own.</param>
/// <param name="IconIndex">Index within <paramref name="IconPath"/>.</param>
internal sealed record JumpListTask(
    string Title,
    string Path,
    string Arguments,
    string IconPath,
    int IconIndex)
{
    /// <summary>Whether this is worth putting in a menu.</summary>
    /// <remarks>
    /// A task with no title cannot be presented — the user would be asked to click a blank line —
    /// and one with no target cannot be run. Both occur: a jump list carries entries the shell
    /// itself filters out before drawing.
    /// </remarks>
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Path);
}
