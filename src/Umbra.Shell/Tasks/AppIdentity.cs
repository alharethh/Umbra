using System.IO;
using System.Text.RegularExpressions;
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
    /// a shared parent, or the same file in two versions of one package (see
    /// <see cref="IsSamePackageUpdated"/>). That is the shape a versioned install has and very
    /// little else does, and
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
            if (Same(Path.GetDirectoryName(deadVersion), Path.GetDirectoryName(liveVersion))) return true;

            return IsSamePackageUpdated(deadPath, livePath);
        }
        catch (ArgumentException)
        {
            // Not path-shaped. An app user model ID need not be, and two of those are either equal
            // or unrelated — there is no "moved" for them.
            return false;
        }
    }

    /// <summary>
    /// A package full name: <c>Name_Version_Architecture_ResourceId_PublisherId</c>, the shape of
    /// every folder under <c>WindowsApps</c>. The resource ID is usually empty, hence the double
    /// underscore in <c>Claude_4.29.0.0_x64__pzs8sxrjxfjjc</c>.
    /// </summary>
    private static readonly Regex PackageFullName = new(
        @"^(?<name>[^_]+)_(?<version>\d+(\.\d+){3})_(?<rest>[^_]+_[^_]*_[a-z0-9]{13})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether two executables are the same file in two versions of one package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A packaged application keeps its version wherever its package folder is, not beside the
    /// executable.</b> Claude went from <c>Claude_3.6556.2.0_x64__pzs8sxrjxfjjc\app\claude.exe</c>
    /// to <c>Claude_4.29.0.0_x64__pzs8sxrjxfjjc\app\claude.exe</c>: the directories above the two
    /// executables are both <c>app</c>, so the sibling rule saw two unrelated trees.
    /// </para>
    /// <para>
    /// Exactly one path segment may differ, it must be a package full name in both, and only its
    /// version may change. Name, architecture, resource and publisher are the package's identity,
    /// and the publisher hash is what keeps a different vendor's package of the same name out.
    /// </para>
    /// </remarks>
    private static bool IsSamePackageUpdated(string deadPath, string livePath)
    {
        string[] dead = deadPath.Split(Path.DirectorySeparatorChar);
        string[] live = livePath.Split(Path.DirectorySeparatorChar);

        if (dead.Length != live.Length) return false;

        int differing = -1;
        for (int i = 0; i < dead.Length; i++)
        {
            if (Same(dead[i], live[i])) continue;
            if (differing >= 0) return false;
            differing = i;
        }

        if (differing < 0) return false;

        Match before = PackageFullName.Match(dead[differing]);
        Match after = PackageFullName.Match(live[differing]);

        return before.Success && after.Success
            && Same(before.Groups["name"].Value, after.Groups["name"].Value)
            && Same(before.Groups["rest"].Value, after.Groups["rest"].Value);
    }
}
