using System.IO;
using System.Runtime.InteropServices;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Shell;

namespace Umbra.Shell.JumpList;

/// <summary>
/// Reads the shell links packed inside a <c>.customDestinations-ms</c> file.
/// </summary>
/// <remarks>
/// <para>
/// <b>The container format is undocumented; the links inside it are not.</b> Rather than reverse
/// engineer the wrapper, this finds each link by its 20-byte header signature and hands the bytes
/// from there to <c>IPersistStream::Load</c> on a real <c>ShellLink</c> object. Windows does the
/// parsing, which means MS-SHLLINK is not reimplemented here and cannot drift from it. The loader
/// reads only as far as the link it is given, so the bytes after one link being the start of the
/// next costs nothing.
/// </para>
/// <para>
/// What is lost by scanning rather than parsing the container is the <i>category</i> each task
/// belongs to — the "Tasks" and "Frequent" headings Windows draws. File order is preserved, which
/// in practice keeps related entries together.
/// </para>
/// <para>
/// The COM plumbing lives in <see cref="ShellLinkInterop"/>, shared with the Start Menu shortcut
/// reader — including the reason titles have to be read type-agnostically.
/// </para>
/// </remarks>
internal static class ShellLinkReader
{
    /// <summary>
    /// A shell link header: <c>HeaderSize</c> 0x4C followed by the link CLSID.
    /// </summary>
    /// <remarks>
    /// Twenty fixed bytes, which is long enough that a false positive in an arbitrary file is not
    /// a practical concern — and a false positive costs a failed <c>Load</c>, not a wrong answer.
    /// </remarks>
    private static readonly byte[] LinkHeader =
    [
        0x4C, 0x00, 0x00, 0x00,
        0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46,
    ];

    /// <summary>Every link in a jump list file, in file order.</summary>
    /// <param name="path">A <c>.customDestinations-ms</c> file.</param>
    /// <param name="limit">Stop after this many. One is enough to identify whose list this is.</param>
    public static List<JumpListTask> Read(string path, int limit = int.MaxValue)
    {
        List<JumpListTask> tasks = [];

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Jump list files are rewritten by the owning application while we read them, so a
            // sharing violation here is ordinary rather than exceptional.
            return tasks;
        }

        foreach (int offset in FindLinks(bytes, limit))
        {
            if (Load(bytes, offset) is { } task) tasks.Add(task);
            if (tasks.Count >= limit) break;
        }

        return tasks;
    }

    /// <summary>Offsets of each shell link header, in order.</summary>
    /// <remarks>Internal rather than private so the scan can be tested without the shell.</remarks>
    internal static List<int> FindLinks(byte[] bytes, int limit = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        List<int> offsets = [];

        for (int i = 0; i + LinkHeader.Length <= bytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < LinkHeader.Length && match; j++)
                if (bytes[i + j] != LinkHeader[j]) match = false;

            if (!match) continue;

            offsets.Add(i);
            if (offsets.Count >= limit) break;

            // Nothing inside a link can start another header, so the scan may skip the header it
            // just matched rather than re-examining its own bytes.
            i += LinkHeader.Length - 1;
        }

        return offsets;
    }

    private static JumpListTask? Load(byte[] bytes, int offset)
    {
        try
        {
            object? link = ShellLinkInterop.Create();
            if (link is null) return null;

            ((ShellLinkInterop.IPersistStream)link).Load(
                ShellLinkInterop.SHCreateMemStream(bytes[offset..], (uint)(bytes.Length - offset)));

            (string target, string arguments, string icon, int iconIndex) = ShellLinkInterop.Describe(link);

            return new JumpListTask(ShellLinkInterop.Title(link), target, arguments, icon, iconIndex);
        }
        catch (COMException e)
        {
            BringUpLog.Write(e, $"jump list: link at offset {offset} would not load");
            return null;
        }
        catch (InvalidCastException e)
        {
            BringUpLog.Write(e, $"jump list: object at offset {offset} is not a shell link");
            return null;
        }
    }
}
