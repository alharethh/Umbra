using System.IO;
using ManagedShell.WindowsTasks;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Which applications have outline mode turned on (design-spec §5.3).
/// </summary>
/// <remarks>
/// <para>
/// Per-application, not global. §5.3 is explicit that outline mode "costs recognisability unevenly
/// and users should be able to apply it to their worst offenders only", and measuring a real icon
/// set bore that out: the saving ranged from 73% down to nothing at all, concentrated in the two
/// icons that are genuinely solid fills.
/// </para>
/// <para>
/// Keyed on the application rather than the window, so the setting survives closing and reopening
/// an app, and applies to all of its windows at once.
/// </para>
/// </remarks>
internal sealed class OutlineSettings
{
    private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    public OutlineSettings(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    /// <summary>Where the choices are kept.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Umbra",
        "outline-icons.txt");

    /// <summary>Raised when any application's setting changes.</summary>
    public event Action? Changed;

    /// <summary>Turns outline mode on for every icon, regardless of the stored choices.</summary>
    public bool ApplyToAll { get; set; }

    /// <summary>Whether this window's application should be outlined.</summary>
    public bool IsEnabled(ApplicationWindow? window)
    {
        if (ApplyToAll) return true;

        string? key = KeyFor(window);
        return key is not null && _enabled.Contains(key);
    }

    /// <summary>Flips the setting for this window's application and persists it.</summary>
    public void Toggle(ApplicationWindow? window)
    {
        string? key = KeyFor(window);
        if (key is null) return;

        if (!_enabled.Add(key)) _enabled.Remove(key);

        BringUpLog.Write($"outline mode {(_enabled.Contains(key) ? "on" : "off")} for {key}");

        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// A stable identity for the application behind a window.
    /// </summary>
    /// <remarks>
    /// The app user model ID is the better key where one exists — it is what Windows itself uses to
    /// group taskbar buttons — and the executable path is the fallback. The window title is
    /// deliberately not used: it changes as the user works, and the setting would evaporate.
    /// </remarks>
    private static string? KeyFor(ApplicationWindow? window)
    {
        if (window is null) return null;

        if (!string.IsNullOrWhiteSpace(window.AppUserModelID)) return window.AppUserModelID;
        if (!string.IsNullOrWhiteSpace(window.WinFileName)) return window.WinFileName;

        return null;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            foreach (string line in File.ReadAllLines(_path))
            {
                string key = line.Trim();
                if (key.Length > 0 && !key.StartsWith('#')) _enabled.Add(key);
            }
        }
        catch (IOException e)
        {
            BringUpLog.Write(e, "outline settings load failed");
        }
        catch (UnauthorizedAccessException e)
        {
            BringUpLog.Write(e, "outline settings load failed");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(
                _path,
                ["# Applications rendered in outline mode (Umbra, design-spec 5.3).", .. _enabled.Order()]);
        }
        catch (IOException e)
        {
            BringUpLog.Write(e, "outline settings save failed");
        }
        catch (UnauthorizedAccessException e)
        {
            BringUpLog.Write(e, "outline settings save failed");
        }
    }
}
