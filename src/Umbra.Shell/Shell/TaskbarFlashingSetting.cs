using Microsoft.Win32;

namespace Umbra.Shell.Shell;

/// <summary>
/// Windows' own "Show flashing on taskbar apps" (Settings → Personalization → Taskbar → Taskbar
/// behaviors), which the attention badge follows.
/// </summary>
/// <remarks>
/// Stored as <c>TaskbarFlashing</c> under Explorer's <c>Advanced</c> key. <b>Absent means on</b>:
/// Windows writes the value only once the switch has been touched, and on this laptop it had never
/// been, so a reader that took a missing value as off would hide every badge for most users. Read
/// when a badge is about to be shown rather than watched, so it costs nothing while nothing asks.
/// </remarks>
internal static class TaskbarFlashingSetting
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "TaskbarFlashing";

    /// <summary>Whether Windows would flash a taskbar button now.</summary>
    public static bool Enabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return Interpret(key?.GetValue(ValueName));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Unreadable is not the user switching it off.
            return true;
        }
    }

    /// <summary>The rule, without the registry: only an explicit zero turns flashing off.</summary>
    internal static bool Interpret(object? value) => value switch
    {
        null => true,
        int i => i != 0,
        _ => true,
    };
}
