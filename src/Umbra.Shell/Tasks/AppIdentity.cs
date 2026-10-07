using System.IO;
using ManagedShell.WindowsTasks;

namespace Umbra.Shell.Tasks;

/// <summary>
/// A stable identity for the application behind a window.
/// </summary>
/// <remarks>
/// <para>
/// The app user model ID is the better key where one exists — it is what Windows itself uses to
/// group taskbar buttons, and it survives an application being updated or moved. The executable
/// path is the fallback.
/// </para>
/// <para>
/// The window title is deliberately never used: it changes as the user works, so a pin keyed on it
/// would evaporate the moment someone opened a different document.
/// </para>
/// </remarks>
internal static class AppIdentity
{
    /// <summary>The identity of a window's application, or null if it has none.</summary>
    public static string? For(ApplicationWindow? window)
    {
        if (window is null) return null;

        if (!string.IsNullOrWhiteSpace(window.AppUserModelID)) return window.AppUserModelID;
        if (!string.IsNullOrWhiteSpace(window.WinFileName)) return window.WinFileName;

        return null;
    }

    /// <summary>
    /// Whether two identities refer to the same application. Paths are compared case-insensitively
    /// because Windows treats them that way and the same executable can arrive spelled either way.
    /// </summary>
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The identity an executable is running under, when that is not its own path.
    /// </summary>
    /// <param name="path">The executable.</param>
    /// <param name="running">Every running window's identity and executable.</param>
    /// <returns>
    /// The one identity windows of <paramref name="path"/> carry, or null when none are open, when
    /// they are keyed by the path anyway, or when they disagree.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>A pin made by dropping an executable has only a path to be keyed by, and the running
    /// application may not be.</b> Firefox, Chrome and most Electron applications set an app user
    /// model ID, which <see cref="For"/> prefers — so dropping <c>firefox.exe</c> on the bar would
    /// pin a path that no Firefox window ever matches, and the bar would draw Firefox twice the
    /// moment it was started. Same failure as an application updating into a new directory, from
    /// the other side.
    /// </para>
    /// <para>
    /// <b>Disagreement is refused rather than resolved.</b> One executable can run under several
    /// identities — Chrome's browser windows and its installed web applications are all
    /// <c>chrome.exe</c> — and guessing would pin the browser as whichever web application happened
    /// to be open. The pin keeps its path and tries again on the next window event.
    /// </para>
    /// </remarks>
    public static string? RunningIdentityFor(string? path, IEnumerable<(string Key, string? Executable)> running)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        List<string> keys = [];
        foreach ((string key, string? executable) in running)
        {
            if (!Same(executable, path)) continue;
            if (!keys.Any(k => Same(k, key))) keys.Add(key);
        }

        if (keys.Count != 1) return null;

        return Same(keys[0], path) ? null : keys[0];
    }

    /// <summary>
    /// Whether a dead path and a live one are the same application, moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The identity of a path-keyed application contains its version.</b> Applications that
    /// update in place install each release into its own directory — Discord went from
    /// <c>Discordpp-1.0.9253\Discord.exe</c> to <c>Discordpp-1.0.9258\Discord.exe</c> — so
    /// after an update the running application does not match the pin, and the bar shows the same
    /// application twice: a pin that can only launch it, and a running icon that cannot be pinned
    /// to. Neither can close it from the other.
    /// </para>
    /// <para>
    /// The rule is deliberately narrow: the same executable file name, in sibling directories under
    /// a shared parent. That is the shape a versioned install has and very little else does, and
    /// the caller only asks about pins whose own path has already gone — so the alternative to
    /// adopting is a permanently broken icon, not a working one.
    /// </para>
    /// </remarks>
    public static bool IsSameApplicationMoved(string? deadPath, string? livePath)
    {
        if (string.IsNullOrWhiteSpace(deadPath) || string.IsNullOrWhiteSpace(livePath)) return false;
        if (Same(deadPath, livePath)) return false;

        try
        {
            if (!Same(Path.GetFileName(deadPath), Path.GetFileName(livePath))) return false;

            string? deadVersion = Path.GetDirectoryName(deadPath);
            string? liveVersion = Path.GetDirectoryName(livePath);

            if (deadVersion is null || liveVersion is null) return false;
            if (Same(deadVersion, liveVersion)) return true;

            return Same(Path.GetDirectoryName(deadVersion), Path.GetDirectoryName(liveVersion));
        }
        catch (ArgumentException)
        {
            // Not path-shaped. An app user model ID need not be, and two of those are either equal
            // or unrelated — there is no "moved" for them.
            return false;
        }
    }
}
