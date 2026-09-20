using System.IO;
using System.Reflection;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Writes the licences out beside the settings, so they exist wherever Umbra does.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because one of the downloads is a single <c>.exe</c> with no files beside it.</b>
/// Umbra is MIT, which asks that its notice travel with copies; ManagedShell is Apache 2.0 and its
/// assemblies are bundled into that file, and Apache 2.0 asks that whoever receives them receives
/// the licence too. A link in a release note is not a copy.
/// </para>
/// <para>
/// Written once and never overwritten: they land next to <c>settings.txt</c>, which is somewhere
/// the user can already be pointed at, and a file the user has edited or replaced is theirs. If
/// writing fails there is nothing to do about it and nothing worth stopping for — the bar is not
/// the licence's enforcement mechanism.
/// </para>
/// </remarks>
internal static class LicenceFiles
{
    private static readonly (string Resource, string FileName)[] Files =
    [
        ("Umbra.LICENSE", "LICENSE.txt"),
        ("Umbra.THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md"),
    ];

    /// <summary>Puts them beside the settings if they are not already there.</summary>
    public static void EnsureWritten(string settingsPath)
    {
        string? directory = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(directory)) return;

        foreach ((string resource, string fileName) in Files)
        {
            string destination = Path.Combine(directory, fileName);
            if (File.Exists(destination)) continue;

            try
            {
                using Stream? source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
                if (source is null)
                {
                    BringUpLog.Write($"licence resource missing from the build: {resource}");
                    continue;
                }

                Directory.CreateDirectory(directory);

                using FileStream file = File.Create(destination);
                source.CopyTo(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                BringUpLog.Write(e, $"could not write {fileName}");
            }
        }
    }
}
