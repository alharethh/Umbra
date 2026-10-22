using System.IO;
using ManagedShell.WindowsTasks;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Tasks;

/// <summary>An application the user has pinned to the bar.</summary>
/// <param name="Key">Its <see cref="AppIdentity"/>.</param>
/// <param name="Path">Executable path, used to launch it and to read its icon when nothing is running.</param>
/// <param name="Name">Display name, for the tooltip.</param>
internal sealed record PinnedApp(string Key, string Path, string Name)
{
    /// <summary>
    /// What a packaged application's pin records as its path: the shell's own name for it, which is
    /// also what Explorer's Run box and <c>explorer.exe</c> accept.
    /// </summary>
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>
    /// Whether this pin is a packaged application, started by its app user model ID rather than
    /// from a file. Also true of a pin made before that was understood, which recorded the UWP
    /// frame host as its executable.
    /// </summary>
    public bool IsPackaged =>
        Path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase) || PackagedAppNames.IsFrameHost(Path);
}

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
    public void Pin(ApplicationWindow window, PackagedAppNames? packagedNames = null)
    {
        string? key = AppIdentity.For(window);
        if (key is null || IsPinned(key)) return;

        string? packagedName = (packagedNames ?? PackagedAppNames.Shared).Lookup(window.AppUserModelID);

        _pins.Add(RecordFor(key, window.WinFileName, window.WinFileDescription, window.Title, packagedName));
        Save();
    }

    /// <summary>
    /// The pin a window makes, from what is known about it. Pure, so it can be tested without a
    /// window.
    /// </summary>
    /// <remarks>
    /// <b>A UWP application's window says nothing about the application.</b> Its executable is
    /// <c>ApplicationFrameHost.exe</c> and its description "Application Frame Host", for Settings,
    /// Calculator and every other one alike. Recording those gave a closed pin that was named after
    /// the frame and, clicked, started the frame host and nothing else. Such a pin records the
    /// Apps-folder path of its key instead, which is never a file, so the launch falls through to
    /// <c>ActivateApplication</c> by app user model ID — which is how Windows starts it.
    /// </remarks>
    internal static PinnedApp RecordFor(string key, string? file, string? description, string? title, string? packagedName)
    {
        if (PackagedAppNames.IsFrameHost(file))
            return new PinnedApp(key, PinnedApp.AppsFolderPrefix + key, TaskbarSlot.NameFor(file, description, key, title, packagedName) ?? key);

        return new PinnedApp(key, file ?? string.Empty, DisplayNameFor(file, description, title));
    }

    /// <summary>
    /// Brings a packaged application's pin into line once its name is known: the Apps-folder path
    /// and the application's own name.
    /// </summary>
    /// <returns>True when something changed and the file was rewritten.</returns>
    /// <remarks>
    /// Repairs pins made before packaged applications were understood, which recorded the frame
    /// host and "Application Frame Host", and a new pin whose name was still the window title
    /// because the shell had not answered yet. Runs inside the model's rebuild, so it writes only
    /// on a change.
    /// </remarks>
    public bool UpdatePackaged(string? key, string? name)
    {
        if (key is null || string.IsNullOrWhiteSpace(name)) return false;

        int index = _pins.FindIndex(p => AppIdentity.Same(p.Key, key));
        if (index < 0 || !_pins[index].IsPackaged) return false;

        PinnedApp healed = _pins[index] with { Path = PinnedApp.AppsFolderPrefix + _pins[index].Key, Name = name.Trim() };
        if (healed == _pins[index]) return false;

        _pins[index] = healed;
        Write();

        BringUpLog.Write($"packaged pin healed: {healed.Key} -> {healed.Name}");
        return true;
    }

    /// <summary>
    /// Pins an executable that was dropped on the bar, with no window to read it from.
    /// </summary>
    /// <param name="key">
    /// The identity to pin it under: the running application's own, when it is already open under
    /// an app user model ID, and otherwise the path itself.
    /// </param>
    /// <param name="path">The executable.</param>
    /// <returns>The key the application is pinned under — the existing one, if it already was.</returns>
    /// <remarks>
    /// Already pinned is matched on the recorded path as well as the key. An application pinned
    /// from its window under an app user model ID has a key that looks nothing like a path, and
    /// dropping its executable on the bar would otherwise pin it a second time beside itself.
    /// </remarks>
    public string PinExecutable(string key, string path)
    {
        PinnedApp? existing = _pins.FirstOrDefault(p => AppIdentity.Same(p.Key, key))
                           ?? _pins.FirstOrDefault(p => AppIdentity.Same(p.Path, path));
        if (existing is not null) return existing.Key;

        _pins.Add(new PinnedApp(key, path, DisplayNameFor(path)));
        Save();

        return key;
    }

    private static string DisplayNameFor(string path)
    {
        try
        {
            string? description = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (FileNotFoundException)
        {
        }

        return Path.GetFileNameWithoutExtension(path);
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

        // A UWP application's window runs from the frame host, which is where it is drawn and not
        // where it lives. "Healing" its pin to that would undo exactly what RecordFor avoids.
        if (PackagedAppNames.IsFrameHost(path)) return false;

        int index = _pins.FindIndex(p => AppIdentity.Same(p.Key, key));
        if (index < 0) return false;

        // The guard above checks what the window ran from; this one checks what the pin is. A
        // packaged pin launches by its id and has no executable to go stale, so every path a window
        // offers it is wrong: a frame-hosted app's CoreWindow surfacing top-level, or a packaged
        // game drawn outside the frame host, reports the exe inside the package under the same id.
        // Taking that would quietly stop the pin being packaged, and UpdatePackaged would never
        // look at it again.
        if (_pins[index].IsPackaged) return false;

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
    /// <param name="oldKey">The identity the pin is recorded under.</param>
    /// <param name="newKey">The identity the running application actually has.</param>
    /// <param name="path">
    /// The executable to record. Defaults to <paramref name="newKey"/>, which is right for a moved
    /// application — its new identity <i>is</i> its new path. Wrong for one adopting an app user
    /// model ID, which is not a path at all, so that caller keeps the old one.
    /// </param>
    public bool Rekey(string oldKey, string newKey, string? path = null)
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

        _pins[index] = _pins[index] with { Key = newKey, Path = path ?? newKey };
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

    private static string DisplayNameFor(string? file, string? description, string? title)
    {
        // The window title is what the user sees, but it is per-document; the file description is
        // the application's own name and is what belongs on a pin that outlives every window.
        if (!string.IsNullOrWhiteSpace(description)) return description;
        if (!string.IsNullOrWhiteSpace(file)) return Path.GetFileNameWithoutExtension(file);

        return title ?? "Application";
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
