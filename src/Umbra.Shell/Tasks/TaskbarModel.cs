using System.Collections.ObjectModel;
using System.IO;
using System.Collections.Specialized;
using System.ComponentModel;
using ManagedShell.WindowsTasks;

namespace Umbra.Shell.Tasks;

/// <summary>
/// Maintains the bar's slots, in one order that covers pinned and running applications alike.
/// </summary>
/// <remarks>
/// <para>
/// Layout comes from <see cref="BarOrder"/>; see it for the rule and for why the row is no longer
/// segregated into a pinned section and a running tail.
/// </para>
/// <para>
/// Slots are updated in place rather than rebuilt. Replacing the collection on every window change
/// would work, but it would also throw away and recreate every icon and every binding each time any
/// window anywhere opened, closed or changed title — which on a busy desktop is constant. It is
/// also what lets the row animate: the panel slides a container to its new position by recognising
/// it, and a container that was destroyed and recreated has nowhere to slide from.
/// </para>
/// </remarks>
internal sealed class TaskbarModel : IDisposable
{
    private readonly ICollectionView _windows;
    private readonly PinSettings _pins;
    private readonly BarOrder _order = new();
    private readonly ShortcutResolver _shortcuts = new();
    private readonly AppIconResolver _icons = new();
    private readonly PackagedAppNames _packagedNames;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    public TaskbarModel(ICollectionView windows, PinSettings pins, PackagedAppNames? packagedNames = null)
    {
        _windows = windows;
        _pins = pins;
        _packagedNames = packagedNames ?? PackagedAppNames.Shared;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        if (_windows is INotifyCollectionChanged observable)
            observable.CollectionChanged += OnWindowsChanged;

        _pins.Changed += OnPinsChanged;
        Rebuild();
    }

    /// <summary>The bar's slots, in display order.</summary>
    public ObservableCollection<TaskbarSlot> Slots { get; } = [];

    /// <summary>
    /// Icon resolution, shared by every slot so that one cache and one wedged-path list serve the
    /// whole row. Exposed for the icon-source setting, which applies live.
    /// </summary>
    public AppIconResolver Icons => _icons;

    /// <summary>
    /// Tells every slot to re-pick its icon. For when the icon-source setting changes.
    /// </summary>
    /// <remarks>
    /// Nothing is re-resolved — both answers are already cached — so this is a repaint rather
    /// than a round of shell calls.
    /// </remarks>
    public void RefreshIcons()
    {
        foreach (TaskbarSlot slot in Slots) slot.RaiseIconChanged();
    }

    /// <summary>
    /// Refreshes which slot owns the foreground window. Called from the bar's existing tick rather
    /// than a timer of its own; Windows does not push foreground changes to us and ManagedShell's
    /// own tracking is not reliable enough to drive the indicator.
    /// </summary>
    public void UpdateForeground(IntPtr foreground)
    {
        foreach (TaskbarSlot slot in Slots) slot.SetForeground(foreground);
    }

    /// <summary>
    /// Moves an application to a new position in the row, for drag-reordering.
    /// </summary>
    /// <param name="key">The application to move. Pinned or not — the row does not distinguish.</param>
    /// <param name="index">Where to move it to, clamped to the row.</param>
    /// <param name="persist">
    /// Whether to write the pin file. A drag passes through every position between its start and
    /// its end, so the moves it makes on the way are applied in memory and <see cref="PersistOrder"/>
    /// is called once on drop.
    /// </param>
    /// <returns>True when the order actually changed.</returns>
    public bool Move(string key, int index, bool persist = true)
    {
        if (!_order.Move(key, index)) return false;

        Rebuild(persist);
        return true;
    }

    /// <summary>Writes the order the user settled on. Called at the end of a drag.</summary>
    public void PersistOrder() => _pins.Persist();

    public void Dispose()
    {
        if (_windows is INotifyCollectionChanged observable)
            observable.CollectionChanged -= OnWindowsChanged;

        _pins.Changed -= OnPinsChanged;

        foreach (TaskbarSlot slot in Slots) slot.Detach();
        Slots.Clear();
    }

    private void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnPinsChanged() => Rebuild();

    private void Rebuild() => Rebuild(persist: true);

    private void Rebuild(bool persist)
    {
        Dictionary<string, List<ApplicationWindow>> running = GroupRunningWindows();

        AdoptMovedApplications(running.Keys);
        AdoptRunningIdentities(running);
        NamePackagedPins();

        _order.Reconcile([.. _pins.Pins.Select(p => p.Key)], running.Keys);

        IReadOnlyList<string> order = _order.Keys;

        for (int index = 0; index < order.Count; index++)
        {
            string key = order[index];
            PinnedApp? pin = _pins.Pins.FirstOrDefault(p => AppIdentity.Same(p.Key, key));
            List<ApplicationWindow> windows = Lookup(running, key);

            TaskbarSlot? existing = Slots.FirstOrDefault(s => AppIdentity.Same(s.Key, key));

            if (existing is null)
            {
                TaskbarSlot slot = new(key, pin, _shortcuts, _icons);
                slot.SetWindows(windows);
                Slots.Insert(Math.Min(index, Slots.Count), slot);
                continue;
            }

            if (!ReferenceEquals(existing.Pin, pin))
            {
                existing.Pin = pin;
                existing.RaisePinChanged();
            }

            existing.SetWindows(windows);

            // A pinned application that is running is the one moment we can see where it really
            // lives, so take it. Writes only when the path actually changed.
            if (pin is not null && windows.Count > 0 && _pins.UpdatePath(key, windows[0].WinFileName))
                existing.RaisePinChanged();

            int current = Slots.IndexOf(existing);
            if (current != index && index < Slots.Count) Slots.Move(current, index);
        }

        // An application that is neither pinned nor running no longer has a place on the bar.
        for (int i = Slots.Count - 1; i >= 0; i--)
        {
            TaskbarSlot slot = Slots[i];
            if (order.Any(k => AppIdentity.Same(k, slot.Key))) continue;

            slot.Detach();
            Slots.RemoveAt(i);
        }

        // The pin file records only pins, so their relative order is read back out of the row —
        // that is the one thing the file can carry across a restart about where the user put them.
        // Written only when it actually changed, because this runs on every window event.
        if (_pins.Reorder(_order.PinnedIn(_pins.IsPinned)) && persist) _pins.Persist();
    }

    /// <summary>
    /// Re-points pins whose application has updated into a new directory.
    /// </summary>
    /// <remarks>
    /// Without this the bar shows such an application twice: a pinned icon that can only launch it,
    /// and a running icon that cannot be pinned — and neither can close it, because the pinned one
    /// believes nothing is running. Runs before the row is reconciled so the adopted identity is
    /// what gets laid out, and touches only pins whose own executable has actually gone.
    /// </remarks>
    private void AdoptMovedApplications(IEnumerable<string> runningKeys)
    {
        List<string> live = [.. runningKeys];
        if (live.Count == 0) return;

        foreach (PinnedApp pin in _pins.Pins.ToList())
        {
            // An Apps-folder path is never a file, and its application never moves: it is keyed by
            // an app user model ID, which carries no version directory.
            if (pin.IsPackaged || PathStillExists(pin.Path)) continue;

            foreach (string key in live)
            {
                if (!AppIdentity.IsSameApplicationMoved(pin.Path, key)) continue;

                if (_pins.Rekey(pin.Key, key)) _order.Rename(pin.Key, key);
                break;
            }
        }
    }

    /// <summary>
    /// Re-keys path-keyed pins whose application turns out to run under an app user model ID.
    /// </summary>
    /// <remarks>
    /// Only a pin made by dropping an executable can be in this state — one made from a window
    /// already carries the window's identity. See <see cref="AppIdentity.RunningIdentityFor"/>.
    /// The path is kept: it is still the executable, and the new key is not a path.
    /// </remarks>
    private void AdoptRunningIdentities(Dictionary<string, List<ApplicationWindow>> running)
    {
        if (running.Count == 0) return;

        List<(string Key, string? Executable)> windows = RunningExecutables(running);

        foreach (PinnedApp pin in _pins.Pins.ToList())
        {
            if (!AppIdentity.Same(pin.Key, pin.Path)) continue;

            string? key = AppIdentity.RunningIdentityFor(pin.Path, windows);
            if (key is null) continue;

            if (_pins.Rekey(pin.Key, key, pin.Path)) _order.Rename(pin.Key, key);
        }
    }

    /// <summary>
    /// Gives packaged applications' pins their own name and Apps-folder path, once the shell has
    /// named them.
    /// </summary>
    /// <remarks>
    /// The pin's key is the app user model ID, so this works for a pin with no window open — the
    /// case that matters, since a closed pin has nothing else to be named from. The name arrives on
    /// a worker; the rebuild it asks for comes back here and finds it.
    /// </remarks>
    private void NamePackagedPins()
    {
        foreach (PinnedApp pin in _pins.Pins.ToList())
        {
            if (!pin.IsPackaged) continue;

            if (_packagedNames.Lookup(pin.Key) is { } name) _pins.UpdatePackaged(pin.Key, name);
            else _packagedNames.Request(pin.Key, OnPackagedNameResolved);
        }
    }

    /// <summary>
    /// A packaged pin's name arrived, on the resolver's worker. A named method rather than a
    /// lambda so that <see cref="PackagedAppNames.Request"/> recognises the same model asking
    /// again on every rebuild and tells it once.
    /// </summary>
    private void OnPackagedNameResolved() => _dispatcher.BeginInvoke(new Action(Rebuild));

    private static List<(string Key, string? Executable)> RunningExecutables(
        Dictionary<string, List<ApplicationWindow>> running) =>
        [.. running.SelectMany(group => group.Value.Select(w => (group.Key, w.WinFileName)))];

    /// <summary>
    /// Pins an executable dropped on the bar and puts it where it was dropped.
    /// </summary>
    /// <param name="path">The executable.</param>
    /// <param name="insertAt">
    /// The gap it was dropped into: 0 is before the first icon, <c>Slots.Count</c> after the last.
    /// </param>
    /// <returns>The key it is pinned under.</returns>
    /// <remarks>
    /// Dropping something already pinned moves it rather than pinning it twice, and dropping an
    /// application that is open but unpinned pins the icon that is already there — under the
    /// identity its windows carry, so the two cannot come apart into separate icons.
    /// </remarks>
    public string PinExecutable(string path, int insertAt)
    {
        Dictionary<string, List<ApplicationWindow>> running = GroupRunningWindows();
        string key = AppIdentity.RunningIdentityFor(path, RunningExecutables(running)) ?? path;

        // Raises Changed when it adds, which rebuilds the row with the new pin on the end.
        key = _pins.PinExecutable(key, path);

        // A gap, not a slot: moving an icon rightwards takes it out of the row before putting it
        // back, so every gap to its right is one fewer by the time it is reinserted.
        int current = _order.IndexOf(key);
        int index = current >= 0 && current < insertAt ? insertAt - 1 : insertAt;

        // Writes the pin file itself when the order moves; when it does not, adding already wrote it.
        Move(key, index);

        return key;
    }

    private static bool PathStillExists(string? path)
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

    private Dictionary<string, List<ApplicationWindow>> GroupRunningWindows()
    {
        Dictionary<string, List<ApplicationWindow>> grouped = new(StringComparer.OrdinalIgnoreCase);

        foreach (object? item in _windows)
        {
            if (item is not ApplicationWindow window) continue;
            if (!window.ShowInTaskbar) continue;

            string? key = AppIdentity.For(window);
            if (key is null) continue;

            if (!grouped.TryGetValue(key, out List<ApplicationWindow>? list))
            {
                list = [];
                grouped[key] = list;
            }

            list.Add(window);
        }

        return grouped;
    }

    private static List<ApplicationWindow> Lookup(
        Dictionary<string, List<ApplicationWindow>> running, string key) =>
        running.TryGetValue(key, out List<ApplicationWindow>? windows) ? windows : [];
}
