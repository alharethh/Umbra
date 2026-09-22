using System.IO;
using Microsoft.Win32;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Whether Umbra starts when the user signs in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The largest functional gap this project had.</b> A taskbar you have to launch by hand after
/// every reboot is not a taskbar — it is a demo of one. Nothing else on the roadmap is reached
/// before someone hits this.
/// </para>
/// <para>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> rather than a scheduled task or a
/// service. It is per-user, needs no elevation, and — the reason that matters here — the user can
/// see and undo it from Task Manager's Startup tab without going near us. A scheduled task would
/// also let us set a delay, which a shell replacement arguably wants, but it is invisible in the
/// place people look when something starts itself and they want it to stop.
/// </para>
/// <para>
/// <b>The stored command is quoted, always.</b> The value is parsed as a command line, so an
/// unquoted path through <c>Program Files</c> starts <c>C:\Program</c> with arguments. Umbra
/// normally lives under a user folder where that does not bite, which is exactly why it would be
/// found late and by somebody else.
/// </para>
/// <para>
/// <b>It is rewritten whenever the executable moves.</b> A single-file build is commonly run from
/// wherever it was downloaded and then moved somewhere permanent; without this the entry keeps
/// pointing at a file that is gone, and Windows silently drops it. So the path is re-checked on
/// every start while the setting is on, and corrected in place.
/// </para>
/// </remarks>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value name under Run. Ours alone; nothing else may be touched.</summary>
    /// <remarks>
    /// <b>Overridable so the tests never touch the real one.</b> They exercise the real registry
    /// — a fake would only assert that this code calls the methods it calls — and a test host
    /// that died mid-run would otherwise leave <c>testhost.exe</c> registered to start at the
    /// developer's next sign-in. Under a name of their own the worst case is a stray value
    /// nothing reads.
    /// </remarks>
    internal const string DefaultValueName = "Umbra";

    /// <summary>Whether Umbra is currently registered to start at sign-in.</summary>
    public static bool IsEnabled(string valueName = DefaultValueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(valueName) is string value && value.Length > 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            BringUpLog.Write(e, "startup: could not read the Run key");
            return false;
        }
    }

    /// <summary>The command currently registered, or null when there is none.</summary>
    public static string? RegisteredCommand(string valueName = DefaultValueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Registers or removes the sign-in entry, to match <paramref name="enabled"/>.
    /// </summary>
    /// <returns>True when the registry was actually changed.</returns>
    public static bool Apply(bool enabled, string valueName = DefaultValueName)
    {
        string? executable = Environment.ProcessPath;

        if (enabled && string.IsNullOrWhiteSpace(executable))
        {
            BringUpLog.Write("startup: cannot register, the process path is unknown");
            return false;
        }

        string wanted = enabled ? Quote(executable!) : string.Empty;

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            string? current = key.GetValue(valueName) as string;

            if (!enabled)
            {
                if (current is null) return false;

                key.DeleteValue(valueName, throwOnMissingValue: false);
                BringUpLog.Write("startup: sign-in entry removed");
                return true;
            }

            // Ordinal, not OrdinalIgnoreCase: a path that differs only in case is the same file
            // on Windows, and rewriting it every start would be a pointless registry write on
            // every launch.
            if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return false;

            key.SetValue(valueName, wanted, RegistryValueKind.String);

            BringUpLog.Write(current is null
                ? $"startup: sign-in entry added, {wanted}"
                : $"startup: sign-in entry repointed, {current} -> {wanted}");

            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            BringUpLog.Write(e, $"startup: could not {(enabled ? "add" : "remove")} the sign-in entry");
            return false;
        }
    }

    /// <summary>
    /// Wraps a path in quotes, as a Run value must be.
    /// </summary>
    /// <remarks>
    /// Internal so the rule can be asserted without touching the registry. A path that already
    /// carries quotes is left alone rather than double-wrapped.
    /// </remarks>
    internal static string Quote(string path)
    {
        string trimmed = path.Trim();

        return trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length > 1
            ? trimmed
            : $"\"{trimmed}\"";
    }
}
