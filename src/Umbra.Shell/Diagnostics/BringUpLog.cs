using System.Globalization;
using System.IO;

namespace Umbra.Shell.Diagnostics;

/// <summary>
/// A plain append-only text log for step 1 bring-up.
/// </summary>
/// <remarks>
/// This exists to answer the empirical tests in shell-integration.md — whether the reserved
/// rectangle Umbra asked for is the one it got, whether maximised windows reflowed, and whether a
/// kill left Explorer's taskbar autohidden. It is not the wear accumulator's persistence layer
/// (spec §9), which is build order step 4 and keeps its own binary format.
/// </remarks>
internal static class BringUpLog
{
    private static readonly Lock Gate = new();

    /// <summary>Where the log lands.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Umbra",
        "bring-up.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(
                    Path,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}"));
            }
        }
        catch (IOException)
        {
            // Diagnostics must never be the reason the bar fails to start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Write(Exception exception, string context) =>
        Write($"{context}: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");
}
