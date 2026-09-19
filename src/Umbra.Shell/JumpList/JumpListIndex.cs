using System.IO;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.JumpList;

/// <summary>
/// Works out which jump list file belongs to which application.
/// </summary>
/// <remarks>
/// <para>
/// Windows names these files after a hash of the application's <c>AppUserModelID</c>. That hash is
/// undocumented, and we do not reliably have an AUMID to feed it anyway — ManagedShell reports a
/// plain executable path for most applications. So the file is identified by its contents instead:
/// the target of its first link is the application it belongs to.
/// </para>
/// <para>
/// <b>An application can have more than one file</b>, left behind by a previous install. Measured
/// on the development machine: two for Firefox, two for Chrome, two for PowerShell. The most
/// recently written one wins, which is the one the live application is maintaining.
/// </para>
/// <para>
/// Cost: reading the first link of all 44 files on the development machine takes about 95 ms. That
/// is too much to spend on every right-click, so the index is built once and then only the files
/// whose timestamps have moved are re-read. Jump lists change often — every document you open —
/// and a full rebuild each time would put that 95 ms back.
/// </para>
/// </remarks>
internal sealed class JumpListIndex
{
    private readonly string _directory;
    private readonly Dictionary<string, Indexed> _files = new(StringComparer.OrdinalIgnoreCase);

    public JumpListIndex(string? directory = null) => _directory = directory ?? DefaultDirectory;

    /// <summary>Where Windows keeps application-published jump list entries.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Recent", "CustomDestinations");

    private sealed record Indexed(DateTime Written, string[] Keys);

    /// <summary>
    /// The tasks an application publishes, or an empty list if it publishes none.
    /// </summary>
    /// <param name="appKey">The slot's <c>AppIdentity</c> — an AUMID or an executable path.</param>
    /// <param name="executablePath">Its executable, where one is known.</param>
    public IReadOnlyList<JumpListTask> TasksFor(string? appKey, string? executablePath)
    {
        string[] candidates = Candidates(appKey, executablePath);
        if (candidates.Length == 0) return [];

        Refresh();

        string? best = null;
        DateTime newest = DateTime.MinValue;

        foreach ((string file, Indexed entry) in _files)
        {
            if (!entry.Keys.Any(k => candidates.Contains(k, StringComparer.OrdinalIgnoreCase))) continue;
            if (entry.Written <= newest) continue;

            best = file;
            newest = entry.Written;
        }

        if (best is null) return [];

        return [.. ShellLinkReader.Read(best).Where(t => t.IsUsable)];
    }

    /// <summary>
    /// The names an application might be known by inside a jump list file.
    /// </summary>
    /// <remarks>
    /// Both the full path and the bare file name, because they fail in opposite directions. The
    /// full path is exact but breaks the moment an application updates into a versioned directory —
    /// which packaged applications do on every release, so one app's path changed under us during
    /// development. The bare name survives that and is what matches a UWP application, whose
    /// "path" inside a link is its AUMID with a nonsense directory prefix.
    /// </remarks>
    internal static string[] Candidates(string? appKey, string? executablePath)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

        foreach (string? value in (string?[])[appKey, executablePath])
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            keys.Add(value);

            try
            {
                string name = Path.GetFileName(value);
                if (!string.IsNullOrWhiteSpace(name)) keys.Add(name);
            }
            catch (ArgumentException)
            {
                // A key that is not path-shaped at all, which an AUMID need not be.
            }
        }

        return [.. keys];
    }

    /// <summary>Brings the index into line with the directory, re-reading only what changed.</summary>
    private void Refresh()
    {
        string[] present;

        try
        {
            if (!Directory.Exists(_directory)) return;
            present = Directory.GetFiles(_directory, "*.customDestinations-ms");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            BringUpLog.Write(e, "jump list: could not list the custom destinations directory");
            return;
        }

        foreach (string file in _files.Keys.Where(f => !present.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList())
            _files.Remove(file);

        foreach (string file in present)
        {
            DateTime written;

            try
            {
                written = File.GetLastWriteTimeUtc(file);
            }
            catch (IOException)
            {
                continue;
            }

            if (_files.TryGetValue(file, out Indexed? known) && known.Written == written) continue;

            // One link is enough to say whose list this is, and reading all of them here would
            // multiply the cost of the index by the length of every jump list on the machine.
            List<JumpListTask> first = ShellLinkReader.Read(file, limit: 1);

            _files[file] = new Indexed(
                written,
                first.Count == 0 ? [] : Candidates(null, first[0].Path));
        }
    }
}
