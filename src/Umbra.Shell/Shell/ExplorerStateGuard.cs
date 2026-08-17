using System.Globalization;
using System.IO;
using ManagedShell.Interop;

namespace Umbra.Shell.Shell;

/// <summary>
/// Captures Explorer's taskbar state before Umbra suppresses it, and puts it back afterwards —
/// including after a hard kill.
/// </summary>
/// <remarks>
/// <para>
/// Suppressing Explorer's taskbar works by setting it to autohide, which is what releases its
/// reserved work area (shell-integration §2.1). ManagedShell restores that on a clean shutdown,
/// but a hard kill leaves the user's taskbar autohidden and effectively invisible until they go
/// and fix it by hand. That is a bad enough failure that it needs to survive process death, not
/// just <c>Dispose</c>.
/// </para>
/// <para>
/// So the pre-Umbra state is written to disk before anything is changed and deleted on a clean
/// exit. A file still present at startup means the previous run died, and its contents — not the
/// current state — are what Explorer gets put back to. Without that ordering the recovery is
/// worse than useless: the state observed at startup after a crash is already <c>AutoHide</c>,
/// so capturing it would record the damage as the thing to restore.
/// </para>
/// <para>
/// Spec §11.2 Q4 widens crash recovery to <c>TaskbarAl</c> as well. Umbra does not write that key
/// until the Start button lands (build order step 8); when it does, it belongs here alongside the
/// taskbar state and in the same file.
/// </para>
/// </remarks>
internal sealed class ExplorerStateGuard : IDisposable
{
    private readonly ITaskbarState _taskbar;
    private readonly string _statePath;
    private bool _restored;

    public ExplorerStateGuard(ITaskbarState taskbar, string? statePath = null)
    {
        ArgumentNullException.ThrowIfNull(taskbar);

        _taskbar = taskbar;
        _statePath = statePath ?? DefaultStatePath;
    }

    /// <summary>Where the pre-Umbra state is parked while Umbra is running.</summary>
    public static string DefaultStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Umbra",
        "explorer-taskbar-state");

    /// <summary>The state Explorer's taskbar was in before Umbra touched it.</summary>
    public NativeMethods.ABState OriginalState { get; private set; }

    /// <summary>Explorer's <c>TaskbarAl</c> before Umbra touched it, or null if it was absent.</summary>
    public int? OriginalAlignment { get; private set; }

    /// <summary>True when startup found leftovers from a run that did not exit cleanly.</summary>
    public bool RecoveredFromUncleanExit { get; private set; }

    /// <summary>
    /// Points Explorer's taskbar alignment at Umbra's own, so the Start menu opens beneath our
    /// Start button rather than wherever Explorer's alignment would have put it (§7.0).
    /// </summary>
    public void ApplyAlignment(int alignment) => _taskbar.SetAlignment(alignment);

    /// <summary>
    /// Repairs the damage from a previous unclean exit, if there was one, then records the state
    /// to restore at the end of this run. Call before setting
    /// <see cref="ExplorerHelper.HideExplorerTaskbar"/>.
    /// </summary>
    public void Capture()
    {
        if (TryReadPersistedState(out NativeMethods.ABState orphaned, out int? orphanedAlignment))
        {
            // A previous run died with Explorer's taskbar autohidden and its alignment overwritten.
            // Put both back before reading the current state, or the damage gets recorded as the
            // baseline and every later run faithfully restores it.
            RecoveredFromUncleanExit = true;

            _taskbar.SetState(orphaned);
            _taskbar.SetAlignment(orphanedAlignment);

            OriginalState = orphaned;
            OriginalAlignment = orphanedAlignment;
        }
        else
        {
            OriginalState = _taskbar.GetState();
            OriginalAlignment = _taskbar.GetAlignment();
        }

        Persist(OriginalState, OriginalAlignment);
    }

    /// <summary>Restores Explorer's taskbar and clears the on-disk marker. Safe to call twice.</summary>
    public void Restore()
    {
        if (_restored) return;
        _restored = true;

        try
        {
            _taskbar.Hidden = false;
            _taskbar.SetState(OriginalState);
            _taskbar.SetAlignment(OriginalAlignment);
        }
        finally
        {
            TryDelete();
        }
    }

    public void Dispose() => Restore();

    /// <remarks>
    /// A missing <c>alignment</c> line means <c>TaskbarAl</c> was absent before Umbra ran, which is
    /// different from it being zero — restoring must delete the value rather than write one.
    /// </remarks>
    private bool TryReadPersistedState(out NativeMethods.ABState state, out int? alignment)
    {
        state = NativeMethods.ABState.Default;
        alignment = null;

        try
        {
            if (!File.Exists(_statePath)) return false;

            bool found = false;

            foreach (string line in File.ReadAllLines(_statePath))
            {
                string[] parts = line.Split('=', 2);
                if (parts.Length != 2) continue;

                string key = parts[0].Trim();
                string value = parts[1].Trim();

                if (key == "state" &&
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state = (NativeMethods.ABState)parsed;
                    found = true;
                }
                else if (key == "alignment" && value.Length > 0 &&
                         int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int align))
                {
                    alignment = align;
                }
            }

            return found;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Persist(NativeMethods.ABState state, int? alignment)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

            List<string> lines =
            [
                "# Explorer state before Umbra started. Present means Umbra is running or died.",
                string.Create(CultureInfo.InvariantCulture, $"state={(int)state}"),
            ];

            if (alignment is not null)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"alignment={alignment.Value}"));

            File.WriteAllLines(_statePath, lines);
        }
        catch (IOException)
        {
            // Recovery is best-effort. Failing to write the marker must not stop the bar starting.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void TryDelete()
    {
        try
        {
            if (File.Exists(_statePath)) File.Delete(_statePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
