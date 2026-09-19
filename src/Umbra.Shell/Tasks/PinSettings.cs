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
/// <para>
/// This is the pinned <i>set</i>, plus enough order to restore a layout. It no longer decides where
/// anything sits on the bar: <see cref="BarOrder"/> does that, for pinned and unpinned icons alike,
/// and hands the pinned subsequence back here to be written. The file therefore follows the row
/// rather than the other way round.
/// </para>
/// <para>
/// Stored as plain text so it can be inspected and edited by hand — a taskbar layout is exactly the
/// sort of thing people want to back up.
/// </para>
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

    /// <summary>
    /// Pins the application behind a running window.
    /// </summary>
    /// <remarks>
    /// Appends, and the position it appends to is not the position the user sees. The icon is
    /// already somewhere on the bar — it has a window open, or it could not have been right-clicked
    /// — and it stays exactly there; the row is rebuilt from <see cref="BarOrder"/>, which does not
    /// consult this list for layout. <see cref="Reorder"/> then writes the pins back in the order
    /// the row shows them. Pinning an icon used to teleport it to the far left for precisely the
    /// reason this paragraph exists to deny.
    /// </remarks>
    public void Pin(ApplicationWindow window)
    {
        string? key = AppIdentity.For(window);
        if (key is null || IsPinned(key)) return;

        _pins.Add(new PinnedApp(key, window.WinFileName ?? string.Empty, DisplayNameFor(window)));
        Save();
    }

    /// <summary>
    /// Corrects a pin's recorded executable after the application has moved.
    /// </summary>
    /// <returns>True when something changed and the file was rewritten.</returns>
    /// <remarks>
    /// Applications that update in place install each release into its own versioned directory, so
    /// a path recorded months ago points at nothing. The running window always knows where it
    /// actually is, so whenever a pinned application is seen running, its pin is brought back into
    /// line. That is what stops the pin going stale again after
    /// <see cref="ShortcutResolver"/> has rescued it once.
    /// </remarks>
    public bool UpdatePath(string? key, string? path)
    {
        if (key is null || string.IsNullOrWhiteSpace(path)) return false;

        int index = _pins.FindIndex(p => AppIdentity.Same(p.Key, key));
        if (index < 0) return false;

        if (string.Equals(_pins[index].Path, path, StringComparison.OrdinalIgnoreCase)) return false;

        _pins[index] = _pins[index] with { Path = path };
        Write();

        BringUpLog.Write($"pin path healed: {_pins[index].Name} -> {path}");
        return true;
    }

    /// <summary>
    /// Points a pin at an application that has moved, identity and all.
    /// </summary>
    /// <returns>True when the pin was rewritten.</returns>
    /// <remarks>
    /// <b>The key has to move too, not just the path.</b> A path-keyed application carries its
    /// version directory in its identity, so after an update the running application no longer
    /// matches the pin — the bar draws it twice, and <see cref="UpdatePath"/> can never fire because
    /// it only runs when a pin is seen running, which is precisely what has stopped happening. Only
    /// rewriting the key breaks that circle.
    /// </remarks>
    public bool Rekey(string oldKey, string newKey)
    {
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey)) return false;
        if (Same(oldKey, newKey)) return false;

        int index = _pins.FindIndex(p => AppIdentity.Same(p.Key, oldKey));
        if (index < 0) return false;

        // Already pinned under the new identity: drop the stale duplicate rather than create two.
        if (_pins.FindIndex(p => AppIdentity.Same(p.Key, newKey)) >= 0)
        {
            _pins.RemoveAt(index);
            Write();
            return true;
        }

        _pins[index] = _pins[index] with { Key = newKey, Path = newKey };
        Write();

        BringUpLog.Write($"pin adopted moved application: {_pins[index].Name} -> {newKey}");
        return true;
    }

    private static bool Same(string? a, string? b) => AppIdentity.Same(a, b);

    /// <summary>Unpins an application. Its windows stay on the bar for as long as they are open.</summary>
    public void Unpin(string? key)
    {
        if (key is null) return;
        if (_pins.RemoveAll(p => AppIdentity.Same(p.Key, key)) == 0) return;

        Save();
    }

    /// <summary>
    /// Reorders the pins to match the row, in memory only.
    /// </summary>
    /// <param name="keys">
    /// The pinned applications in the order the bar shows them — <see cref="BarOrder.PinnedIn"/>
    /// produces exactly this. Any pin the row does not name keeps its place at the end rather than
    /// being dropped: the file is the user's layout and must not lose entries to a row that has not
    /// caught up.
    /// </param>
    /// <returns>True when the order actually changed.</returns>
    /// <remarks>
    /// Writing is deliberately not part of this. A drag crosses every position between where it
    /// started and where it ends, and writing each of them would put a disk write on a mouse-move
    /// handler for orders the user never chose. The drag reorders as the pointer moves and calls
    /// <see cref="Persist"/> once on drop.
    /// </remarks>
    public bool Reorder(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        List<PinnedApp> reordered = [];

        foreach (string key in keys)
        {
            PinnedApp? pin = _pins.FirstOrDefault(p => AppIdentity.Same(p.Key, key));
            if (pin is not null && !reordered.Contains(pin)) reordered.Add(pin);
        }

        reordered.AddRange(_pins.Where(p => !reordered.Contains(p)));

        if (reordered.SequenceEqual(_pins)) return false;

        _pins.Clear();
        _pins.AddRange(reordered);

        return true;
    }

    /// <summary>Writes the current order to disk. Used at the end of a drag.</summary>
    /// <remarks>
    /// Does not raise <see cref="Changed"/>. The caller is the thing that changed the order, and
    /// telling it to rebuild the row it has just finished arranging would be a round trip for
    /// nothing.
    /// </remarks>
    public void Persist() => Write();

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

    /// <summary>Writes the file and tells the bar the pins changed.</summary>
    private void Save()
    {
        Write();
        Changed?.Invoke();
    }

    private void Write()
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
    }
}
