using System.IO;
using System.Windows.Media;

namespace Umbra.Shell.Shell;

/// <summary>
/// One line in the settings window's list of notification icons.
/// </summary>
/// <param name="Identifier">ManagedShell's identifier, as <see cref="TrayChoice"/> stores it.</param>
/// <param name="Name">What the user sees.</param>
/// <param name="IsRunning">False for an icon chosen earlier whose application is not running now.</param>
/// <param name="OnBar">Whether it is chosen for the bar.</param>
/// <param name="Matches">Compares a stored entry with this icon.</param>
/// <param name="Icon">Its picture, when it is running.</param>
internal sealed record TrayIconRow(
    string Identifier,
    string Name,
    bool IsRunning,
    bool OnBar,
    Func<string, bool> Matches,
    ImageSource? Icon)
{
    /// <summary>An icon the notification area holds right now.</summary>
    internal sealed record Running(string Identifier, string Name, Func<string, bool> Matches, ImageSource? Icon);

    /// <summary>
    /// The rows: the chosen icons first, in bar order, then the rest by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A chosen icon that is not running is still listed, marked so, or there would be no way to
    /// take it off the bar short of starting its application.
    /// </para>
    /// <para>
    /// An entry matches the first running icon it names and no other, so an application with two
    /// icons gets two rows and a stored entry is never shown twice.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TrayIconRow> For(TrayChoice choice, IReadOnlyList<Running> running)
    {
        List<TrayIconRow> rows = [];
        HashSet<Running> listed = [];

        foreach (string entry in choice.OnBar)
        {
            Running? icon = running.FirstOrDefault(r => !listed.Contains(r) && r.Matches(entry));

            if (icon is not null)
            {
                listed.Add(icon);
                rows.Add(new TrayIconRow(icon.Identifier, icon.Name, true, true, icon.Matches, icon.Icon));
            }
            else
            {
                rows.Add(new TrayIconRow(
                    entry,
                    NameOfAbsent(entry),
                    IsRunning: false,
                    OnBar: true,
                    Matches: other => string.Equals(other, entry, StringComparison.OrdinalIgnoreCase),
                    Icon: null));
            }
        }

        foreach (Running icon in running.Where(r => !listed.Contains(r)).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            rows.Add(new TrayIconRow(icon.Identifier, icon.Name, true, false, icon.Matches, icon.Icon));

        return rows;
    }

    /// <summary>
    /// A name for an icon that is not running, read out of its identifier.
    /// </summary>
    /// <remarks>
    /// An icon without a GUID is stored as <c>path:uid:title</c>, so its title or its file name is
    /// there to show. An icon with a GUID is stored as nothing but the GUID, and nothing about it
    /// can be said until it comes back.
    /// </remarks>
    internal static string NameOfAbsent(string identifier)
    {
        if (Guid.TryParse(identifier, out _)) return "An icon that is not running";

        // The path has its own colon after the drive letter, so look for ":<uid>:" past it.
        int search = identifier.Length > 2 && identifier[1] == ':' ? 2 : 0;
        int first = identifier.IndexOf(':', search);
        int second = first < 0 ? -1 : identifier.IndexOf(':', first + 1);

        if (first < 0 || second < 0) return identifier;

        string path = identifier[..first];
        string title = identifier[(second + 1)..].Trim();

        string name = title.Length > 0 ? title : Path.GetFileNameWithoutExtension(path);
        return $"{name} (not running)";
    }
}
