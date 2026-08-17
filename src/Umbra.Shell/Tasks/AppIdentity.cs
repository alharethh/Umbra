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
/// The window title is deliberately never used: it changes as the user works, so a pin or an
/// outline setting keyed on it would evaporate the moment someone opened a different document.
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
}
