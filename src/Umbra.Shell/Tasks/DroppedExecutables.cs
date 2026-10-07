using System.IO;
using System.Runtime.InteropServices;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tasks;

/// <summary>
/// Which of the files dragged onto the bar can be pinned, and what each one pins.
/// </summary>
/// <remarks>
/// <para>
/// An <c>.exe</c> pins itself. A <c>.lnk</c> pins what it points at, because a Start Menu or
/// desktop shortcut is what most people actually have to hand — but only when that is an
/// executable started <b>without arguments</b>. A pin records a path and nothing else, so a
/// shortcut like Discord's (<c>Update.exe --processStart Discord.exe</c>) would be pinned as the
/// updater, and a browser shortcut carrying <c>--profile-directory</c> would quietly lose its
/// profile. Refusing those is honest; pinning something that launches differently from the
/// shortcut the user dropped is not.
/// </para>
/// <para>
/// Everything else is ignored rather than refused as a whole: dropping a selection that happens to
/// include a text file still pins the programs in it.
/// </para>
/// </remarks>
internal static class DroppedExecutables
{
    /// <summary>The executables the dropped files name, in drop order, without repeats.</summary>
    public static IReadOnlyList<string> From(IEnumerable<string>? files)
    {
        List<string> found = [];
        if (files is null) return found;

        foreach (string file in files)
        {
            string? executable = Resolve(file);
            if (executable is null) continue;
            if (found.Any(f => AppIdentity.Same(f, executable))) continue;

            found.Add(executable);
        }

        return found;
    }

    private static string? Resolve(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;

        try
        {
            string extension = Path.GetExtension(file);

            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return File.Exists(file) ? Path.GetFullPath(file) : null;

            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                return FromShortcut(file);
        }
        catch (ArgumentException)
        {
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static string? FromShortcut(string file)
    {
        if (!File.Exists(file)) return null;

        try
        {
            object? link = ShellLinkInterop.Create();
            if (link is null) return null;

            ((ShellLinkInterop.IPersistFile)link).Load(file, 0);
            (string target, string arguments, _, _) = ShellLinkInterop.Describe(link);

            if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(target)) return null;

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                BringUpLog.Write($"drop: not pinning {Path.GetFileName(file)}, it passes arguments a pin cannot carry: {arguments}");
                return null;
            }

            return Path.GetFullPath(target);
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }
}
