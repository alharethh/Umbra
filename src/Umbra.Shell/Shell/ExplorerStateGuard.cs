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

    /// <summary>True when startup found leftovers from a run that did not exit cleanly.</summary>
    public bool RecoveredFromUncleanExit { get; private set; }

    /// <summary>
    /// Repairs the damage from a previous unclean exit, if there was one, then records the state
    /// to restore at the end of this run. Call before setting
    /// <see cref="ExplorerHelper.HideExplorerTaskbar"/>.
    /// </summary>
    public void Capture()
    {
        if (TryReadPersistedState(out NativeMethods.ABState orphaned))
        {
            // A previous run died with Explorer's taskbar autohidden. Put it back before reading
            // the current state, or the damage gets recorded as the baseline.
            RecoveredFromUncleanExit = true;
            _taskbar.SetState(orphaned);
            OriginalState = orphaned;
        }
        else
        {
            OriginalState = _taskbar.GetState();
        }

        Persist(OriginalState);
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
        }
        finally
        {
            TryDelete();
        }
    }

    public void Dispose() => Restore();

    private bool TryReadPersistedState(out NativeMethods.ABState state)
    {
        state = NativeMethods.ABState.Default;

        try
        {
            if (!File.Exists(_statePath)) return false;

            string text = File.ReadAllText(_statePath).Trim();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return false;

            state = (NativeMethods.ABState)value;
            return true;
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

    private void Persist(NativeMethods.ABState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(
                _statePath,
                ((int)state).ToString(CultureInfo.InvariantCulture));
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
