using System.Reflection;
using ManagedShell.AppBar;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Asks ManagedShell, once the bars are up, whether a fullscreen application is already in front.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bar that started under a fullscreen application drew over it until the user switched
/// windows.</b> On Windows 8 and later <c>FullScreenHelper</c> looks only when something tells it
/// to: a window activating, the desktop activating, a window moving monitor, or Windows' own
/// fullscreen notification. A bar starting while a game holds the foreground hears none of those,
/// so the list stays empty and the bar stays topmost over the game. Measured 2026-11-09 with a
/// borderless window in front and the bar restarted under it: the bar was still drawn over the
/// window 38 s later, and gave way only when the window lost the foreground. Startup is not the
/// rare case it sounds: the hang watch relaunches the bar and a monitor change rebuilds the set,
/// and both happen mid-game.
/// </para>
/// <para>
/// <b>Asking is ManagedShell's own check, called once</b>, rather than a second detector of ours.
/// A fullscreen app we recognised and ManagedShell did not would never leave its list, so nothing
/// would ever raise the bar again. Its <c>updateFullScreenWindows</c> is private, hence the
/// reflection; a ManagedShell update that renames it is caught by a test and logged here, and the
/// bar then behaves exactly as it did before this existed.
/// </para>
/// </remarks>
internal static class FullScreenRecheck
{
    /// <summary>ManagedShell's private re-evaluation, or null if this version has none.</summary>
    internal static MethodInfo? Method { get; } = typeof(FullScreenHelper).GetMethod(
        "updateFullScreenWindows",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null);

    /// <summary>Runs ManagedShell's check against the current foreground window.</summary>
    /// <remarks>On the UI thread: it changes the collection the bars listen to.</remarks>
    public static void Run(FullScreenHelper helper)
    {
        if (Method is null)
        {
            BringUpLog.Write("fullscreen recheck: FullScreenHelper.updateFullScreenWindows not found in this ManagedShell");
            return;
        }

        try
        {
            Method.Invoke(helper, null);
            BringUpLog.Write($"fullscreen recheck at startup: {helper.FullScreenApps.Count} fullscreen app(s) in front");
        }
        catch (TargetInvocationException e)
        {
            BringUpLog.Write($"fullscreen recheck failed: {e.InnerException?.GetType().Name}: {e.InnerException?.Message}");
        }
    }
}
