using System.IO;

namespace Umbra.Shell.Tasks;

/// <summary>What a look at a pinned application found.</summary>
internal enum Presence
{
    /// <summary>It is there, or there is still a way to start it.</summary>
    Present,

    /// <summary>Nothing is left to start: as far as anything can tell, it was uninstalled.</summary>
    Gone,

    /// <summary>Cannot be told: a drive that is not there, an API that failed. Never acted on.</summary>
    Unknown,
}

/// <summary>
/// Whether a pinned application is still installed, for removing the pins of uninstalled ones.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why:</b> Windows removes a pin when its application is uninstalled. Umbra kept it, as an empty
/// slot that took a place in the row and started nothing (measured on master, 2026-11-15: three
/// such pins drew nothing for at least 75 s). The owner's call: remove it as Windows does, once the
/// application is confirmed gone. <see cref="DeadPinWatch"/> does the confirming; this is one look.
/// </para>
/// <para>
/// <b>Gone is the answer that removes a pin, so it is the one that has to be earned.</b> Anything
/// that could still start the application, and anything that cannot be checked, says otherwise:
/// </para>
/// <list type="bullet">
/// <item>A packaged application (Store or UWP) is asked about by <b>family</b>, fresh from Windows.
/// A family stays installed all through an update, while a version's folder disappears, so a path
/// that looks missing mid-update is never the evidence. Not <see cref="PackagedExecutable.Locate"/>,
/// whose misses are remembered for 30 s.</item>
/// <item>An ordinary application whose recorded file has gone may still be installed somewhere
/// else: a Start Menu shortcut whose target exists (Discord's <c>Update.exe</c>), or the same file
/// in a sibling version folder (<c>app-1.0.9258</c> beside <c>app-1.0.9253</c>). The shortcut index
/// is never refreshed, so a shortcut only counts if its target is still on disk.</item>
/// <item>A path on a drive that is not there (a USB stick, a network share, a locked volume) is
/// unknown, not gone: the drive will come back.</item>
/// </list>
/// <para>Whether the application is running is the model's to say; it never asks about one that is.</para>
/// </remarks>
internal sealed class PinPresence(PackagedExecutable packages, ShortcutResolver shortcuts)
{
    private bool _waitedForIndex;

    /// <summary>
    /// Whether a look since the last call answered Unknown only because the Start Menu was still
    /// being read, and clears it. The model then looks again soon rather than at the next window
    /// event, which on an idle desktop after sign-in may be a long way off.
    /// </summary>
    public bool TakeWaitedForIndex()
    {
        bool waited = _waitedForIndex;
        _waitedForIndex = false;
        return waited;
    }

    public Presence Of(PinnedApp pin)
    {
        if (pin.IsPackaged)
            return FamilyOf(pin.Key) is { } family ? FromInstalled(packages.AnyInstalled(family)) : Presence.Unknown;

        string path = pin.Path;
        if (!IsFullPath(path)) return Presence.Unknown;
        if (Exists(path)) return Presence.Present;

        if (!DriveIsThere(path)) return Presence.Unknown;

        if (PackagedExecutable.PackageRoot(path) is { } root)
            return FromInstalled(packages.AnyInstalled(root.Family));

        // Reading the Start Menu takes every .lnk on the machine and this runs on the UI thread,
        // so the index is built elsewhere and the question waits for the next look.
        if (!shortcuts.IsIndexed)
        {
            shortcuts.IndexInBackground();
            _waitedForIndex = true;
            return Presence.Unknown;
        }

        if (shortcuts.Resolve(pin.Name, path) is { } shortcut && Exists(shortcut.Target))
            return Presence.Present;

        return SiblingVersion(path);
    }

    /// <summary>The family of an app user model ID, <c>Family!App</c>.</summary>
    internal static string? FamilyOf(string appId)
    {
        int bang = appId.IndexOf('!');
        return bang > 0 ? appId[..bang] : null;
    }

    private static Presence FromInstalled(bool? installed) => installed switch
    {
        true => Presence.Present,
        false => Presence.Gone,
        null => Presence.Unknown,
    };

    /// <summary>
    /// Gone unless the same file sits in a folder beside the recorded one, the shape a versioned
    /// install leaves (<see cref="AppIdentity.IsSameApplicationMoved"/>).
    /// </summary>
    private static Presence SiblingVersion(string path)
    {
        string? parent = Path.GetDirectoryName(Path.GetDirectoryName(path));
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return Presence.Gone;

        try
        {
            string file = Path.GetFileName(path);
            foreach (string folder in Directory.EnumerateDirectories(parent))
            {
                string candidate = Path.Combine(folder, file);
                if (Exists(candidate) && AppIdentity.IsSameApplicationMoved(path, candidate)) return Presence.Present;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Presence.Unknown;
        }

        return Presence.Gone;
    }

    private static bool IsFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool DriveIsThere(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && Directory.Exists(root);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool Exists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            return File.Exists(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
