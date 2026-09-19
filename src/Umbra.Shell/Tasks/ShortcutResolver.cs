using System.IO;
using System.Runtime.InteropServices;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tasks;

/// <summary>Where an application really lives, according to its Start Menu shortcut.</summary>
/// <param name="Target">What to run. Often a stable launcher rather than the application itself.</param>
/// <param name="Arguments">What the shortcut passes it.</param>
/// <param name="IconPath">Where the shortcut says its icon is, which may be a dedicated .ico.</param>
internal sealed record AppShortcut(string Target, string Arguments, string IconPath);

/// <summary>
/// Finds an application again after it has moved.
/// </summary>
/// <remarks>
/// <para>
/// A pin records the executable path of the window it was created from. That path is not stable:
/// applications that update in place — Discord, Slack, Teams, anything built on Squirrel — install
/// each release into its own versioned directory and delete the old one. The pin then points at
/// nothing, and a pinned application that is not running has <b>only</b> that path to work from. It
/// draws as an empty placeholder and does nothing when clicked. Observed exactly that way:
/// Discord's pin held <c>app-1.0.9253</c> while the disk had moved on to <c>app-1.0.9258</c>.
/// </para>
/// <para>
/// <b>Windows does not have this problem because it does not pin the versioned path.</b> Discord's
/// Start Menu shortcut targets <c>Update.exe</c> — a launcher that stays put across releases — and
/// names a dedicated <c>app.ico</c>. So the way to find an application that has moved is to ask the
/// same shortcut the Start Menu asks.
/// </para>
/// <para>
/// Consulted only when the recorded path has actually gone missing. While the path is good it is
/// the better answer: it is the executable the user's own window was running from, rather than
/// whatever a shortcut of the same name happens to point at.
/// </para>
/// </remarks>
internal sealed class ShortcutResolver
{
    private readonly string[] _roots;
    private Dictionary<string, AppShortcut>? _shortcuts;

    public ShortcutResolver(params string[] roots) =>
        _roots = roots.Length > 0 ? roots : DefaultRoots;

    /// <summary>The per-user and all-users Start Menus.</summary>
    public static string[] DefaultRoots =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
    ];

    /// <summary>
    /// The shortcut for an application, or null if none can be found.
    /// </summary>
    /// <param name="displayName">The pin's display name, e.g. "Discord".</param>
    /// <param name="executablePath">Its recorded executable, which may no longer exist.</param>
    /// <remarks>
    /// Matched on the shortcut's own file name first. Matching on the target would be the obvious
    /// choice and is the one that does not work: Discord's shortcut targets <c>Update.exe</c>, which
    /// is nothing like the <c>Discord.exe</c> the pin recorded. The name is what survives.
    /// </remarks>
    public AppShortcut? Resolve(string? displayName, string? executablePath)
    {
        Dictionary<string, AppShortcut> shortcuts = Shortcuts();
        if (shortcuts.Count == 0) return null;

        foreach (string? candidate in Names(displayName, executablePath))
            if (candidate is not null && shortcuts.TryGetValue(candidate, out AppShortcut? found))
                return found;

        return null;
    }

    /// <summary>The names a shortcut for this application might be filed under.</summary>
    internal static IEnumerable<string?> Names(string? displayName, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(displayName)) yield return displayName.Trim();

        if (string.IsNullOrWhiteSpace(executablePath)) yield break;

        string? stem = null;

        try
        {
            stem = Path.GetFileNameWithoutExtension(executablePath);
        }
        catch (ArgumentException)
        {
            // Not path-shaped. An AUMID need not be.
        }

        if (!string.IsNullOrWhiteSpace(stem)) yield return stem;
    }

    /// <summary>Every Start Menu shortcut, by name, read once.</summary>
    /// <remarks>
    /// Cached for the life of the process rather than watched. This is consulted only for pins
    /// whose recorded path has gone missing, which is rare; a shortcut appearing mid-session for an
    /// application that was already broken is not worth a file watcher.
    /// </remarks>
    private Dictionary<string, AppShortcut> Shortcuts()
    {
        if (_shortcuts is not null) return _shortcuts;

        _shortcuts = new Dictionary<string, AppShortcut>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in _roots)
        {
            if (!Directory.Exists(root)) continue;

            string[] files;

            try
            {
                files = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                BringUpLog.Write(e, $"shortcuts: could not enumerate {root}");
                continue;
            }

            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);

                // First writer wins, and the per-user Start Menu is searched first: a shortcut the
                // user has is a better answer than one the machine has.
                if (_shortcuts.ContainsKey(name)) continue;
                if (Read(file) is { } shortcut) _shortcuts[name] = shortcut;
            }
        }

        BringUpLog.Write($"shortcuts: indexed {_shortcuts.Count} Start Menu entries");
        return _shortcuts;
    }

    private static AppShortcut? Read(string file)
    {
        try
        {
            object? link = ShellLinkInterop.Create();
            if (link is null) return null;

            ((ShellLinkInterop.IPersistFile)link).Load(file, 0);

            (string path, string arguments, string iconPath, _) = ShellLinkInterop.Describe(link);
            if (string.IsNullOrWhiteSpace(path)) return null;

            return new AppShortcut(path, arguments, iconPath);
        }
        catch (COMException)
        {
            // A shortcut to something that no longer resolves. Common in a real Start Menu.
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }
}
