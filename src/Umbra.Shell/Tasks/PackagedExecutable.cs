using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Tasks;

/// <summary>
/// What a packaged application's executable path stands for: its app user model ID and where the
/// installed version of it lives now, worked out from the path alone, without a running process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed: a packaged executable can carry no icon at all.</b> WhatsApp's
/// <c>WhatsApp.Root.exe</c> has none (measured: <c>ExtractIconEx</c> counts 0), and asking the
/// shell for its icon then answers with Windows' generic program icon and calls that a success.
/// Windows' own taskbar takes the logo from the package by app ID.
/// </para>
/// <para>
/// <b>And a pin's path goes stale.</b> A package installs each version into its own folder,
/// <c>Name_Version_Arch_Resource_PublisherId</c>, and deletes the old one. A pin made under
/// WhatsApp 2.2600 names a folder that is gone once WhatsApp has updated while closed. Measured on
/// master before this: the slot drew nothing, and a click asked Windows to activate the old path
/// and nothing started (the same happened to the owner's Claude pin on 2026-10-29). Only the
/// folder's version changes: the <b>family name</b>, its first and last parts, is the package's
/// identity across versions, and Windows can say where the installed version of a family is.
/// </para>
/// <para>
/// So: family name and the executable's path inside the package, both read off the path; the
/// installed package folders for that family, from Windows; the highest version whose manifest
/// lists the application. The manifest gives the app ID (<c>family!Id</c>) and the executable's
/// current path. The folder the path names is used as well when Windows names none, which keeps a
/// pin working if the API is unavailable.
/// </para>
/// </remarks>
internal sealed class PackagedExecutable
{
    /// <summary>The process-wide instance, asking Windows for installed packages.</summary>
    public static PackagedExecutable Shared { get; } = new(new InstalledPackages());

    /// <summary>How long a path that resolved to nothing is not asked about again.</summary>
    /// <remarks>
    /// The model asks on every rebuild, which is every window event on the desktop, and an
    /// uninstalled application's pin would otherwise send a query to Windows each time. Short,
    /// because a package can be between versions for a moment while it updates.
    /// </remarks>
    private static readonly TimeSpan MissRetry = TimeSpan.FromSeconds(30);

    private readonly IPackageFolders _folders;
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<string, Located> _found = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _missed = new(StringComparer.OrdinalIgnoreCase);

    public PackagedExecutable(IPackageFolders folders, Func<long>? clock = null)
    {
        _folders = folders;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>A packaged executable as it is installed now.</summary>
    /// <param name="AppId">The app user model ID, <c>family!Id</c>.</param>
    /// <param name="Executable">The executable's path in the installed version.</param>
    internal sealed record Located(string AppId, string Executable);

    /// <summary>The app ID for an executable inside a package, or null for anything else.</summary>
    public string? AppIdFor(string? executable) => Locate(executable)?.AppId;

    /// <summary>
    /// Whether anything of a package family is installed, asked fresh: true, false, or null when
    /// that cannot be told.
    /// </summary>
    /// <remarks>
    /// Never cached, unlike <see cref="Locate"/>'s misses, because a false here is what removes a
    /// pin. The family, not a version's folder, because a family stays installed all through an
    /// update: the old version is registered until the new one is.
    /// </remarks>
    public bool? AnyInstalled(string family)
    {
        try
        {
            return _folders.AnyInstalled(family);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            BringUpLog.Write(e, $"packages: could not ask whether {family} is installed");
            return null;
        }
    }

    /// <summary>
    /// Where this packaged executable is installed now, or null when it is not a packaged
    /// executable or its package is not installed.
    /// </summary>
    /// <remarks>
    /// Reads a manifest the first time, so not for a tight loop on the UI thread; after that a
    /// path is remembered for as long as the executable it resolved to still exists.
    /// </remarks>
    public Located? Locate(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;

        if (_found.TryGetValue(executable, out Located? known))
        {
            if (Exists(known.Executable)) return known;
            _found.TryRemove(executable, out _);
        }

        if (_missed.TryGetValue(executable, out long when) && _clock() - when < MissRetry.TotalMilliseconds)
            return null;

        Located? located = Resolve(executable);

        if (located is null) _missed[executable] = _clock();
        else
        {
            _missed.TryRemove(executable, out _);
            _found[executable] = located;
        }

        return located;
    }

    private Located? Resolve(string executable)
    {
        if (PackageRoot(executable) is not { } root) return null;

        string relative = Path.GetRelativePath(root.Folder, executable);

        // The installed versions first, highest first; the folder the path names last, for when
        // Windows names none. Duplicates are harmless: the first that answers wins.
        List<string> folders = [.. OrderByVersionDescending(SafeFolders(root.Family)), root.Folder];

        foreach (string folder in folders)
        {
            if (ReadManifest(folder) is not { } manifest) continue;

            if (Match(manifest, root.Family, relative) is not { } app) continue;

            // The same executable if this version still has it, else the one the manifest names:
            // an update may rename it, and the running window will then be the manifest's.
            string same = Path.Combine(folder, relative);
            string executableNow = Exists(same) ? same : Path.Combine(folder, Normalise(app.Executable));

            if (!Exists(executableNow)) continue;
            return new Located(app.AppId, executableNow);
        }

        return null;
    }

    private IEnumerable<string> SafeFolders(string family)
    {
        try
        {
            return _folders.FoldersOf(family);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            BringUpLog.Write(e, $"packages: could not list the installed versions of {family}");
            return [];
        }
    }

    /// <summary>Highest version first, read from each folder's package full name.</summary>
    internal static IEnumerable<string> OrderByVersionDescending(IEnumerable<string> folders) =>
        folders.OrderByDescending(f => AppIdentity.VersionOf(Path.GetFileName(f)) ?? new Version(0, 0));

    private static string? ReadManifest(string folder)
    {
        try
        {
            string path = Path.Combine(folder, "AppxManifest.xml");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The package folder an executable sits in and the package's family name, from the path alone.
    /// The folder need not exist.
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
    internal static string? AppIdFrom(string manifestXml, string family, string relativeExecutable) =>
        Match(manifestXml, family, relativeExecutable)?.AppId;

    /// <summary>The matching application's ID and its declared executable.</summary>
    /// <remarks>
    /// When nothing names the executable but the package has exactly one application, that one:
    /// a launcher can start a helper the manifest does not list, and with one application there is
    /// nothing else it could belong to. With several, no guess. A manifest that is not XML, or a
    /// package with no applications (a resource or framework package of the same family), answers
    /// nothing.
    /// </remarks>
    private static (string AppId, string Executable)? Match(string manifestXml, string family, string relativeExecutable)
    {
        XDocument manifest;
        try
        {
            manifest = XDocument.Parse(manifestXml);
        }
        catch (XmlException)
        {
            return null;
        }

        string wanted = Normalise(relativeExecutable).ToLowerInvariant();

        List<XElement> applications = manifest.Descendants()
            .Where(e => e.Name.LocalName == "Application" && !string.IsNullOrWhiteSpace((string?)e.Attribute("Id")))
            .ToList();

        XElement? match = applications.FirstOrDefault(a =>
            a.Attribute("Executable") is { } exe && Normalise(exe.Value).ToLowerInvariant() == wanted);

        match ??= applications.Count == 1 ? applications[0] : null;
        if (match is null) return null;

        return ($"{family}!{match.Attribute("Id")!.Value}", (string?)match.Attribute("Executable") ?? relativeExecutable);
    }

    private static string Normalise(string path) => path.Replace('/', '\\').TrimStart('\\');
}

/// <summary>Where the installed versions of a package family live.</summary>
internal interface IPackageFolders
{
    IEnumerable<string> FoldersOf(string familyName);

    /// <summary>
    /// Whether anything of this family is installed for the user: true, false, or null when that
    /// cannot be told. Only a definite false may ever be acted on as "uninstalled".
    /// </summary>
    /// <remarks>
    /// The default reads <see cref="FoldersOf"/>, for test stand-ins whose lists are exact.
    /// <see cref="InstalledPackages"/> answers from the API's own result instead, because its
    /// <see cref="FoldersOf"/> lists nothing on an error too.
    /// </remarks>
    bool? AnyInstalled(string familyName)
    {
        try
        {
            return FoldersOf(familyName).Any();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }
}

/// <summary>
/// Asks Windows: <c>GetPackagesByPackageFamily</c> for the full names installed for this user,
/// then <c>GetPackagePathByFullName</c> for each one's folder.
/// </summary>
internal sealed class InstalledPackages : IPackageFolders
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    public IEnumerable<string> FoldersOf(string familyName)
    {
        List<string> folders = [];

        foreach (string fullName in FullNamesOf(familyName))
        {
            uint length = 0;
            if (GetPackagePathByFullName(fullName, ref length, null) != ErrorInsufficientBuffer || length == 0) continue;

            StringBuilder path = new((int)length);
            if (GetPackagePathByFullName(fullName, ref length, path) == ErrorSuccess) folders.Add(path.ToString());
        }

        return folders;
    }

    /// <remarks>
    /// Measured on the laptop, 2026-11-15: an installed family answers
    /// <c>ERROR_INSUFFICIENT_BUFFER</c> with a count, one that is not installed answers
    /// <c>ERROR_SUCCESS</c> with a count of 0, and a malformed name <c>ERROR_INVALID_PARAMETER</c>.
    /// Only the second is "not installed"; everything else that is not the first is unknown.
    /// </remarks>
    public bool? AnyInstalled(string familyName)
    {
        uint count = 0;
        uint bufferLength = 0;

        int result = GetPackagesByPackageFamily(familyName, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero);
        return result switch
        {
            ErrorInsufficientBuffer when count > 0 => true,
            ErrorSuccess when count == 0 => false,
            _ => null,
        };
    }

    private static List<string> FullNamesOf(string familyName)
    {
        uint count = 0;
        uint bufferLength = 0;

        int first = GetPackagesByPackageFamily(familyName, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero);
        if (first == ErrorSuccess || count == 0) return [];
        if (first != ErrorInsufficientBuffer) return [];

        // The names point into the buffer, so both are unmanaged and stay put for the call.
        IntPtr names = Marshal.AllocHGlobal((int)count * IntPtr.Size);
        IntPtr buffer = Marshal.AllocHGlobal((int)bufferLength * sizeof(char));

        try
        {
            if (GetPackagesByPackageFamily(familyName, ref count, names, ref bufferLength, buffer) != ErrorSuccess) return [];

            List<string> result = [];
            for (int i = 0; i < count; i++)
            {
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size)) is { Length: > 0 } name) result.Add(name);
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(names);
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(
        string packageFamilyName, ref uint count, IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, StringBuilder? path);
}
