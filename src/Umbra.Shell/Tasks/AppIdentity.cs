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
