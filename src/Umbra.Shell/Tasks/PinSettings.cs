using System.IO;
using ManagedShell.WindowsTasks;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Tasks;

/// <summary>An application the user has pinned to the bar.</summary>
/// <param name="Key">Its <see cref="AppIdentity"/>.</param>
/// <param name="Path">Executable path, used to launch it and to read its icon when nothing is running.</param>
/// <param name="Name">Display name, for the tooltip.</param>
internal sealed record PinnedApp(string Key, string Path, string Name);

/// <summary>
/// The pinned applications, in bar order.
/// </summary>
/// <remarks>
/// Order is the order of the lines in the file, which is also the order the user dragged them into.
/// Stored as plain text so it can be inspected and edited by hand — there is no settings UI yet,
/// and even once there is, a taskbar layout is exactly the sort of thing people want to back up.
/// </remarks>
internal sealed class PinSettings
{
    private readonly List<PinnedApp> _pins = [];
    private readonly string _path;

    public PinSettings(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    /// <summary>Where the pins are kept.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Umbra",
        "pinned-apps.txt");

    /// <summary>Raised when the set or order of pins changes.</summary>
    public event Action? Changed;

    /// <summary>The pinned applications, in bar order.</summary>
    public IReadOnlyList<PinnedApp> Pins => _pins;

    /// <summary>Whether this application is pinned.</summary>
    public bool IsPinned(string? key) => key is not null && _pins.Any(p => AppIdentity.Same(p.Key, key));

    /// <summary>Pins the application behind a running window.</summary>
    public void Pin(ApplicationWindow window)
    {
        string? key = AppIdentity.For(window);
        if (key is null || IsPinned(key)) return;

        _pins.Add(new PinnedApp(key, window.WinFileName ?? string.Empty, DisplayNameFor(window)));
        Save();
    }

    /// <summary>Unpins an application. Its windows stay on the bar for as long as they are open.</summary>
    public void Unpin(string? key)
    {
        if (key is null) return;
        if (_pins.RemoveAll(p => AppIdentity.Same(p.Key, key)) == 0) return;

        Save();
    }

    /// <summary>Moves a pinned application to a new position, for drag-reordering.</summary>
    public void Move(string key, int index)
    {
        int current = _pins.FindIndex(p => AppIdentity.Same(p.Key, key));
        if (current < 0) return;

        index = Math.Clamp(index, 0, _pins.Count - 1);
        if (index == current) return;

        PinnedApp pin = _pins[current];
        _pins.RemoveAt(current);
        _pins.Insert(index, pin);
        Save();
    }

    private static string DisplayNameFor(ApplicationWindow window)
    {
        // The window title is what the user sees, but it is per-document; the file description is
        // the application's own name and is what belongs on a pin that outlives every window.
        if (!string.IsNullOrWhiteSpace(window.WinFileDescription)) return window.WinFileDescription;
        if (!string.IsNullOrWhiteSpace(window.WinFileName)) return Path.GetFileNameWithoutExtension(window.WinFileName);

        return window.Title ?? "Application";
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            foreach (string line in File.ReadAllLines(_path))
            {
                if (line.StartsWith('#') || line.Trim().Length == 0) continue;

                string[] parts = line.Split('|');
                if (parts.Length < 1 || parts[0].Trim().Length == 0) continue;

                _pins.Add(new PinnedApp(
                    parts[0].Trim(),
                    parts.Length > 1 ? parts[1].Trim() : string.Empty,
                    parts.Length > 2 ? parts[2].Trim() : parts[0].Trim()));
            }
        }
        catch (IOException e)
        {
            BringUpLog.Write(e, "pin settings load failed");
        }
        catch (UnauthorizedAccessException e)
        {
            BringUpLog.Write(e, "pin settings load failed");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            List<string> lines = ["# Pinned applications, in bar order. key|path|name"];
            lines.AddRange(_pins.Select(p => $"{p.Key}|{p.Path}|{p.Name}"));

            File.WriteAllLines(_path, lines);
        }
        catch (IOException e)
        {
            BringUpLog.Write(e, "pin settings save failed");
        }
        catch (UnauthorizedAccessException e)
        {
            BringUpLog.Write(e, "pin settings save failed");
        }

        Changed?.Invoke();
    }
}
