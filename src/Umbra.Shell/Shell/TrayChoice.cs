using System.IO;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Which notification icons the user has taken out of the overflow and put on the bar, in the
/// order they sit there, and whether every icon is shown on the bar regardless.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own file, not <c>settings.txt</c>.</b> Three places change it — dragging an icon, the
/// chevron's menu and the settings window — and two of them are the bar. Kept in the settings
/// file, the settings window's copy of the settings would save a stale list back over a drag
/// made while it was open, which is the bug #49 fixed for the other settings. One object owns
/// this list for the whole process, and everything that changes it goes through it.
/// </para>
/// <para>
/// <b>Entries are ManagedShell's identifiers</b> (<c>NotifyIcon.Identifier</c>): the icon's GUID
/// when it has one, otherwise <c>path:uid:title</c>. They are compared with
/// <c>NotifyIcon.IsEqualByIdentifier</c>, which is why every lookup here takes a predicate rather
/// than comparing strings: an icon whose title has changed since it was chosen still matches on
/// its path and uid. An entry stays when its application is not running, so the icon comes back
/// to the same place the next time it appears.
/// </para>
/// <para>
/// The four system indicators are not in this list. They are always on the bar
/// (<see cref="ShellHost.SystemIndicators"/>) and <see cref="PinnedFor"/> puts them first.
/// </para>
/// </remarks>
/// <summary>An icon as the bar draws it: its identifier, and how to recognise it in the list.</summary>
internal readonly record struct TrayEntry(string Identifier, Func<string, bool> Matches);

internal sealed class TrayChoice
{
    private const string ShowAllKey = "showAll";

    private readonly List<string> _onBar = [];
    private readonly string _path;

    public TrayChoice(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Umbra",
        "tray-on-bar.txt");

    /// <summary>Raised after any change, once it has been saved.</summary>
    public event Action? Changed;

    /// <summary>The chosen icons' identifiers, in bar order.</summary>
    public IReadOnlyList<string> OnBar => _onBar;

    /// <summary>
    /// Every icon on the bar and none behind the chevron. The chosen list is kept as it is, so
    /// turning this off puts back exactly what the user had chosen.
    /// </summary>
    public bool ShowAll { get; private set; }

    /// <summary>Whether the icon is one the user put on the bar.</summary>
    public bool IsChosen(Func<string, bool> matches) => IndexOf(matches) >= 0;

    /// <summary>The icon's place in <see cref="OnBar"/>, or -1 when it was not chosen.</summary>
    public int PositionOf(Func<string, bool> matches) => IndexOf(matches);

    /// <summary>Whether the icon is drawn on the bar rather than behind the chevron.</summary>
    public bool ShowsOnBar(Func<string, bool> matches) => ShowAll || IsChosen(matches);

    /// <summary>
    /// What ManagedShell's pinned list should be: the system indicators, then the chosen icons.
    /// </summary>
    public string[] PinnedFor(IEnumerable<string> systemIndicators) => [.. systemIndicators, .. _onBar];

    /// <summary>
    /// Puts an icon on the bar at <paramref name="index"/> in <see cref="OnBar"/>, or moves it there
    /// if it is already on it. <paramref name="index"/> is counted before the icon is taken out of
    /// its old place, as a drop position is: the gap it was dropped into.
    /// </summary>
    /// <returns>True when the list changed.</returns>
    public bool Place(string identifier, Func<string, bool> matches, int index)
    {
        if (!Move(identifier, matches, ref index)) return false;

        Commit($"tray on bar: placed {identifier} at {index}");
        return true;
    }

    /// <summary>
    /// A drop on the bar in front of <paramref name="before"/> (null: the end of the row).
    /// </summary>
    /// <param name="drawn">The bar's row as drawn, in order.</param>
    /// <remarks>
    /// With "show all" on, the row is the chosen icons and then the rest in arrival order, and only
    /// the chosen ones have a place in the list. A drop among the rest would land wherever the list
    /// happened to put it (dropped at the end, an icon stayed put), so the row as drawn is written
    /// into the list first and the drop then moves within that.
    /// </remarks>
    /// <returns>True when the list changed.</returns>
    public bool PlaceOnBar(string identifier, Func<string, bool> matches, Func<string, bool>? before, IEnumerable<TrayEntry> drawn)
    {
        bool adopted = ShowAll && AdoptDrawn(drawn);
        int index = IndexBefore(before);
        bool moved = Move(identifier, matches, ref index);
        if (!adopted && !moved) return false;

        Commit($"tray on bar: placed {identifier} at {index}");
        return true;
    }

    /// <summary>
    /// A drop on the chevron or into its menu: the icon goes behind the chevron.
    /// </summary>
    /// <param name="drawn">The bar's row as drawn, in order.</param>
    /// <remarks>
    /// With "show all" on, a drop here used to be refused without a word: the switch overrides the
    /// list, so there was no way to say "all but this one". Now the drop ends "show all" and turns
    /// what was on the bar into the list, less this icon, so nothing else moves. Turning "show all"
    /// on again shows every icon as before.
    /// </remarks>
    /// <returns>True when the list changed.</returns>
    public bool PutBehindChevron(Func<string, bool> matches, IEnumerable<TrayEntry> drawn)
    {
        if (!ShowAll) return Remove(matches);

        AdoptDrawn(drawn);
        _onBar.RemoveAll(entry => matches(entry));
        ShowAll = false;
        Commit("tray on bar: an icon dropped in the menu ended show all; the rest stay on the bar");
        return true;
    }

    /// <summary>Adds every drawn icon not yet in the list to its end, in drawn order.</summary>
    private bool AdoptDrawn(IEnumerable<TrayEntry> drawn)
    {
        bool changed = false;
        foreach (TrayEntry entry in drawn)
        {
            if (IndexOf(entry.Matches) >= 0) continue;
            _onBar.Add(entry.Identifier);
            changed = true;
        }
        return changed;
    }

    /// <summary><see cref="Place"/> without saving. <paramref name="index"/> comes back as inserted.</summary>
    private bool Move(string identifier, Func<string, bool> matches, ref int index)
    {
        int old = IndexOf(matches);
        index = Math.Clamp(index, 0, _onBar.Count);

        if (old >= 0)
        {
            // Dropped into the gap on either side of itself: nothing moves.
            if (index == old || index == old + 1) return false;

            _onBar.RemoveAt(old);
            if (index > old) index--;
        }

        _onBar.Insert(index, identifier);
        return true;
    }

    /// <summary>Appends an icon to the bar if it is not on it already.</summary>
    public bool Add(string identifier, Func<string, bool> matches) =>
        !IsChosen(matches) && Place(identifier, matches, _onBar.Count);

    /// <summary>Puts an icon back behind the chevron.</summary>
    /// <returns>True when the list changed.</returns>
    public bool Remove(Func<string, bool> matches)
    {
        // Every match, not the first: an old file edited by hand may name an icon twice.
        int removed = _onBar.RemoveAll(entry => matches(entry));
        if (removed == 0) return false;

        Commit("tray on bar: removed an icon");
        return true;
    }

    /// <summary>Removes an entry by its exact text, for an icon that is not running.</summary>
    public bool RemoveEntry(string identifier) =>
        Remove(entry => string.Equals(entry, identifier, StringComparison.OrdinalIgnoreCase));

    public void SetShowAll(bool showAll)
    {
        if (ShowAll == showAll) return;

        ShowAll = showAll;
        Commit($"tray on bar: show all = {showAll}");
    }

    /// <summary>
    /// Where in <see cref="OnBar"/> a drop lands, given the icon it was dropped in front of.
    /// </summary>
    /// <param name="before">Matches the icon now drawn after the gap, or null for the end of the row.</param>
    /// <remarks>
    /// The bar draws only the chosen icons that are running, so a position among the drawn icons is
    /// not a position in the list. Inserting in front of the icon after the gap is right whatever
    /// lies hidden between them.
    /// </remarks>
    public int IndexBefore(Func<string, bool>? before)
    {
        if (before is null) return _onBar.Count;

        int index = IndexOf(before);
        return index < 0 ? _onBar.Count : index;
    }

    private int IndexOf(Func<string, bool> matches) => _onBar.FindIndex(entry => matches(entry));

    private void Commit(string log)
    {
        Save();
        BringUpLog.Write(log);
        Changed?.Invoke();
    }

    private void Load()
    {
        string[] lines;

        try
        {
            lines = File.ReadAllLines(_path, TextEncoding.Utf8);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BringUpLog.Write($"tray on bar: could not read {_path}: {ex.Message}");
            return;
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (TryReadShowAll(line, out bool showAll))
            {
                ShowAll = showAll;
                continue;
            }

            if (!_onBar.Contains(line, StringComparer.OrdinalIgnoreCase)) _onBar.Add(line);
        }
    }

    /// <summary>
    /// Reads the one setting line. Anything else is an identifier, so this is strict: a line that
    /// merely starts with the word is not taken for the setting.
    /// </summary>
    private static bool TryReadShowAll(string line, out bool value)
    {
        value = false;

        int equals = line.IndexOf('=');
        if (equals < 0) return false;
        if (!line[..equals].Trim().Equals(ShowAllKey, StringComparison.OrdinalIgnoreCase)) return false;

        return bool.TryParse(line[(equals + 1)..].Trim(), out value);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(
                _path,
                [$"{ShowAllKey} = {ShowAll.ToString().ToLowerInvariant()}", .. _onBar],
                TextEncoding.Utf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BringUpLog.Write($"tray on bar: could not save {_path}: {ex.Message}");
        }
    }
}
