using System.Collections.Concurrent;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Umbra.Shell.Tasks;

/// <summary>
/// The app user model ID of an executable that lives in a package, read from the package's own
/// manifest, so it is known without a running process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed: a packaged executable can carry no icon at all.</b> WhatsApp's
/// <c>WhatsApp.Root.exe</c> has none (measured: <c>ExtractIconEx</c> counts 0), and asking the
/// shell for its icon then answers with Windows' generic program icon and calls that a success,
/// which is what the bar drew. Windows' own taskbar takes the logo from the package by app ID.
/// A running process can be asked its ID (<see cref="ProcessAppId"/>); a pin whose application is
/// closed has only the path it recorded, and this turns that path into the ID.
/// </para>
/// <para>
/// The folder an executable sits under is the package full name,
/// <c>Name_Version_Arch_Resource_PublisherId</c>, and the family name is its first and last parts.
/// The manifest beside it lists each application's <c>Id</c> and <c>Executable</c>; the ID is
/// <c>family!Id</c>.
/// </para>
/// </remarks>
internal static class PackagedExecutable
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app ID for an executable inside a package, or null for anything else.</summary>
    /// <remarks>Reads a file, so call it off the UI thread. Remembered per path.</remarks>
    public static string? AppIdFor(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;

        return Cache.GetOrAdd(executable, static path =>
        {
            if (PackageRoot(path) is not { } root) return null;

            try
            {
                string manifest = File.ReadAllText(Path.Combine(root.Folder, "AppxManifest.xml"));
                return AppIdFrom(manifest, root.Family, Path.GetRelativePath(root.Folder, path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
            {
                return null;
            }
        });
    }

    /// <summary>
    /// The package folder an executable sits in and the package's family name, from the path alone.
    /// </summary>
    internal static (string Folder, string Family)? PackageRoot(string executable)
    {
        string? directory = Path.GetDirectoryName(executable);

        // The nearest folder whose name is a package full name: an executable can sit deeper,
        // as Claude's does in "app".
        for (string? folder = directory; !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (AppIdentity.FamilyNameOf(Path.GetFileName(folder)) is { } family) return (folder, family);
        }

        return null;
    }

    /// <summary>
    /// The app ID of the application in a manifest that runs this executable. Pure, so it can be
    /// tested without a package.
    /// </summary>
    /// <param name="relativeExecutable">The executable's path relative to the package folder.</param>
    /// <remarks>
    /// When nothing names the executable but the package has exactly one application, that one:
    /// a launcher can start a helper the manifest does not list, and with one application there is
    /// nothing else it could belong to. With several, no guess.
    /// </remarks>
    internal static string? AppIdFrom(string manifestXml, string family, string relativeExecutable)
    {
        XDocument manifest = XDocument.Parse(manifestXml);
        string wanted = Normalise(relativeExecutable);

        List<XElement> applications = manifest.Descendants()
            .Where(e => e.Name.LocalName == "Application" && e.Attribute("Id") is not null)
            .ToList();

        XElement? match = applications.FirstOrDefault(a =>
            a.Attribute("Executable") is { } exe && Normalise(exe.Value) == wanted);

        match ??= applications.Count == 1 ? applications[0] : null;

        return match is null ? null : $"{family}!{match.Attribute("Id")!.Value}";
    }

    private static string Normalise(string path) => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
}
