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
/// <para>
/// <b>An Explorer restart under the bar saves the suppression as the user's own setting</b>
/// (QA 2026-10-16, case 0.6): the saved auto-hide bit went <c>0x02</c> → <c>0x03</c> within 15 s
/// of a restarted Explorer starting, and stayed there after a clean exit that had put the
/// <i>runtime</i> state back correctly. Most likely ManagedShell's watchdog autohides the new
/// taskbar while it is still initialising, and a starting Explorer saves its layout. Restoring the
/// runtime state does not trigger a save, so the user would sign in next time to an auto-hidden
/// taskbar, long after uninstalling Umbra — and the next launch would capture the damage as their
/// baseline. So the saved bit is captured too, and put back at <see cref="Restore"/> and in crash
/// recovery, <b>only when the Explorer running the shell is not the one that was running at
/// capture</b>. Without that condition a deliberate change the user makes in Windows Settings while
/// the bar runs would be reverted on exit. Owner's decision, 2026-10-16 (option A).
/// </para>
/// </remarks>
internal sealed class ExplorerStateGuard : IDisposable
{
    private readonly ITaskbarState _taskbar;
    private readonly string _statePath;
    private readonly Func<TimeSpan, Action, IDisposable> _startTimer;
    private bool _restored;

    /// <param name="startTimer">
    /// Runs a callback once after a delay; disposing it cancels a callback not yet queued. A seam
    /// for tests, which need to fire a timer late on purpose. Defaults to a thread-pool timer.
    /// </param>
    public ExplorerStateGuard(
        ITaskbarState taskbar,
        string? statePath = null,
        Func<TimeSpan, Action, IDisposable>? startTimer = null)
    {
        ArgumentNullException.ThrowIfNull(taskbar);

        _taskbar = taskbar;
        _statePath = statePath ?? DefaultStatePath;
        _startTimer = startTimer ?? StartThreadPoolTimer;
    }

    private static IDisposable StartThreadPoolTimer(TimeSpan due, Action callback) =>
        new Timer(_ => callback(), null, due, Timeout.InfiniteTimeSpan);

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
    /// The auto-hide setting Explorer had saved before Umbra touched it, or null when it could not
    /// be read in a known format — in which case it is never written either.
    /// </summary>
    public bool? OriginalSavedAutoHide { get; private set; }

    /// <summary>True once this guard has rewritten the saved auto-hide bit.</summary>
    public bool RepairedSavedAutoHide { get; private set; }

    /// <summary>The Explorer this run's baseline was taken against. See <see cref="ITaskbarState.GetShellIdentity"/>.</summary>
    private string? _shellAtCapture;

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
        if (TryReadPersistedState(out PersistedState orphaned))
        {
            // A previous run died with Explorer's taskbar autohidden and its alignment overwritten.
            // Put both back before reading the current state, or the damage gets recorded as the
            // baseline and every later run faithfully restores it.
            RecoveredFromUncleanExit = true;

            _taskbar.SetState(orphaned.State);
            _taskbar.SetAlignment(orphaned.Alignment);

            OriginalState = orphaned.State;
            OriginalAlignment = orphaned.Alignment;
            OriginalSavedAutoHide = orphaned.SavedAutoHide;

            // The dead run's Explorer is the one to compare against: if it has been replaced since,
            // the replacement may have saved the suppression as the user's setting.
            RepairSavedAutoHideIfExplorerRestarted(orphaned.Shell);
        }
        else
        {
            OriginalState = _taskbar.GetState();
            OriginalAlignment = _taskbar.GetAlignment();
            OriginalSavedAutoHide = _taskbar.GetSavedAutoHide();
        }

        // From here on, a restart means a restart during this run.
        _shellAtCapture = _taskbar.GetShellIdentity();

        Persist();
    }

    /// <summary>How long after an Explorer restart its saved settings are watched.</summary>
    /// <remarks>
    /// Explorer's own startup save landed at +6.5 s and +7.6 s in two measured restarts, after the
    /// bar's reservation had already settled, so the window cannot simply end when that does. A
    /// notification costs nothing while it waits, so the window is generous; a write after it is
    /// still put right at exit, as before.
    /// </remarks>
    public static readonly TimeSpan LateSaveWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Repairs per watch. Bounds a ping-pong in case Explorer ever answers our write with its own.
    /// </summary>
    public const int MaxRepairsPerWatch = 5;

    private readonly Lock _gate = new();
    private IDisposable? _lateSaveWatch;
    private IDisposable? _lateSaveTimer;
    private int _repairsThisWatch;

    /// <summary>
    /// Called when Explorer has restarted under the bar. Puts the saved auto-hide bit back now and
    /// on every write Explorer makes to its saved settings for <paramref name="window"/>, rather
    /// than only at exit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// QA 2026-10-18, case 0.6: the restarted Explorer saved the suppression as the user's setting
    /// 7.6 s after starting and nothing touched it again until the bar exited. Anything that ends
    /// the session without <see cref="Restore"/> or the next launch's crash recovery — a kill
    /// followed by never running Umbra again, an uninstall — left the user's taskbar set to hide.
    /// </para>
    /// <para>
    /// Watching rather than checking once, because the save comes when Explorer gets round to it:
    /// a check at the moment the bar's reservation settled (+3.4 s) would have found nothing
    /// wrong, and the 0x03 would have arrived four seconds later. The repair is the same one
    /// <see cref="Restore"/> makes, with the same conditions, so it fires only while the Explorer
    /// running is not the one captured.
    /// </para>
    /// <para>
    /// <b>A change the user makes in Windows Settings inside the window is put back too</b>, up to
    /// <see cref="MaxRepairsPerWatch"/> times. A notification says only that the key was written,
    /// not by whom, and the user's toggle and Explorer's startup save are the same write to the
    /// same byte. The exit repair has always reverted that change under the same condition, so the
    /// watch moves it earlier rather than making it new; and while the bar runs, Explorer's taskbar
    /// is suppressed either way, so the setting has nothing to act on until exit. Backlog 1i.
    /// </para>
    /// </remarks>
    public void WatchForLateSave(TimeSpan window)
    {
        IDisposable? previous;

        lock (_gate)
        {
            if (_restored) return;

            previous = DetachLateSaveWatch();
            _repairsThisWatch = 0;
        }

        // Outside the lock: ending a watch waits for its thread, which may be waiting for the lock.
        previous?.Dispose();

        // The watch calls back once straight away and that call takes the lock, so it is started
        // outside it too, then kept only if nothing restored or replaced it in the meantime.
        IDisposable? watch = _taskbar.WatchSavedSettings(OnSavedSettingsChanged);
        if (watch is null) return;

        lock (_gate)
        {
            if (!_restored && _lateSaveWatch is null)
            {
                _lateSaveWatch = watch;
                // The timer is told which watch it ends. Disposing a timer does not withdraw a
                // callback already queued, so a window that expired just as the next restart
                // replaced its watch would otherwise end the new one, two minutes early.
                IDisposable mine = watch;
                _lateSaveTimer = _startTimer(window, () => EndLateSaveWatch(mine));
                watch = null;
            }
        }

        watch?.Dispose();
    }

    /// <summary>Whether a late-save watch is running.</summary>
    public bool WatchingForLateSave
    {
        get
        {
            lock (_gate) return _lateSaveWatch is not null;
        }
    }

    private void OnSavedSettingsChanged()
    {
        lock (_gate)
        {
            if (_restored || _repairsThisWatch >= MaxRepairsPerWatch) return;

            if (RepairSavedAutoHideIfExplorerRestarted(_shellAtCapture)) _repairsThisWatch++;
        }
    }

    private void EndLateSaveWatch(IDisposable expired)
    {
        IDisposable? watch;

        lock (_gate)
        {
            // A late callback from a watch already replaced or ended: not ours to end.
            if (!ReferenceEquals(_lateSaveWatch, expired)) return;
            watch = DetachLateSaveWatch();
        }

        watch?.Dispose();
    }

    /// <summary>Under the lock. The caller disposes what it returns, outside the lock.</summary>
    private IDisposable? DetachLateSaveWatch()
    {
        _lateSaveTimer?.Dispose();
        _lateSaveTimer = null;

        IDisposable? watch = _lateSaveWatch;
        _lateSaveWatch = null;
        return watch;
    }

    /// <summary>Restores Explorer's taskbar and clears the on-disk marker. Safe to call twice.</summary>
    public void Restore()
    {
        IDisposable? watch;

        lock (_gate)
        {
            if (_restored) return;
            _restored = true;
            watch = DetachLateSaveWatch();
        }

        watch?.Dispose();

        try
        {
            _taskbar.Hidden = false;
            _taskbar.SetState(OriginalState);
            _taskbar.SetAlignment(OriginalAlignment);

            // After the runtime state, so that if setting it ever does make Explorer save, the
            // repaired bit is the last word.
            RepairSavedAutoHideIfExplorerRestarted(_shellAtCapture);
        }
        finally
        {
            TryDelete();
        }
    }

    public void Dispose() => Restore();

    /// <summary>
    /// Puts the saved auto-hide bit back, but only when the Explorer running now is not
    /// <paramref name="shellBefore"/>, and only if it differs.
    /// </summary>
    /// <remarks>
    /// Every unknown answers "leave it alone": no recorded setting (a marker from an older build, or
    /// an unfamiliar format), no recorded Explorer, no Explorer now, or an unreadable setting now.
    /// This is a backstop for one known failure, and the cost of it misfiring is reverting a choice
    /// the user made, so it fires on evidence rather than on absence of it.
    /// </remarks>
    private bool RepairSavedAutoHideIfExplorerRestarted(string? shellBefore)
    {
        if (OriginalSavedAutoHide is not bool original || shellBefore is null) return false;

        string? shellNow = _taskbar.GetShellIdentity();
        if (shellNow is null || shellNow == shellBefore) return false;

        bool? saved = _taskbar.GetSavedAutoHide();
        if (saved is null || saved == original) return false;

        _taskbar.SetSavedAutoHide(original);
        RepairedSavedAutoHide = true;

        Diagnostics.BringUpLog.Write(
            $"explorer restarted under the bar ({shellBefore} -> {shellNow}) and saved auto-hide " +
            $"{(saved.Value ? "on" : "off")}; put back to {(original ? "on" : "off")}");
        return true;
    }

    /// <summary>What a marker file holds.</summary>
    private readonly record struct PersistedState(
        NativeMethods.ABState State, int? Alignment, bool? SavedAutoHide, string? Shell);

    /// <remarks>
    /// A missing <c>alignment</c> line means <c>TaskbarAl</c> was absent before Umbra ran, which is
    /// different from it being zero — restoring must delete the value rather than write one. A
    /// missing <c>autohide</c> or <c>shell</c> line — a marker from a build before either existed,
    /// or a value that could not be read — means the saved bit is left alone.
    /// </remarks>
    private bool TryReadPersistedState(out PersistedState persisted)
    {
        NativeMethods.ABState state = NativeMethods.ABState.Default;
        int? alignment = null;
        bool? savedAutoHide = null;
        string? shell = null;
        persisted = default;

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
                else if (key == "autohide" && value is "0" or "1")
                {
                    savedAutoHide = value == "1";
                }
                else if (key == "shell" && value.Length > 0)
                {
                    shell = value;
                }
            }

            persisted = new PersistedState(state, alignment, savedAutoHide, shell);
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

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

            List<string> lines =
            [
                "# Explorer state before Umbra started. Present means Umbra is running or died.",
                string.Create(CultureInfo.InvariantCulture, $"state={(int)OriginalState}"),
            ];

            if (OriginalAlignment is not null)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"alignment={OriginalAlignment.Value}"));

            // The saved auto-hide bit, and which Explorer was running when this run took its
            // baseline, so crash recovery can tell whether a restart since may have overwritten it.
            if (OriginalSavedAutoHide is bool autoHide)
                lines.Add(autoHide ? "autohide=1" : "autohide=0");

            if (_shellAtCapture is not null)
                lines.Add("shell=" + _shellAtCapture);

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
