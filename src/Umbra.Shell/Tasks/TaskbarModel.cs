using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ManagedShell.WindowsTasks;

namespace Umbra.Shell.Tasks;

/// <summary>
/// Maintains the bar's slots: pinned applications first in their pinned order, then anything else
/// that happens to be running.
/// </summary>
/// <remarks>
/// Slots are updated in place rather than rebuilt. Replacing the collection on every window change
/// would work, but it would also throw away and recreate every icon and every binding each time any
/// window anywhere opened, closed or changed title — which on a busy desktop is constant.
/// </remarks>
internal sealed class TaskbarModel : IDisposable
{
    private readonly ICollectionView _windows;
    private readonly PinSettings _pins;

    public TaskbarModel(ICollectionView windows, PinSettings pins)
    {
        _windows = windows;
        _pins = pins;

        if (_windows is INotifyCollectionChanged observable)
            observable.CollectionChanged += OnWindowsChanged;

        _pins.Changed += Rebuild;
        Rebuild();
    }

    /// <summary>The bar's slots, in display order.</summary>
    public ObservableCollection<TaskbarSlot> Slots { get; } = [];

    /// <summary>
    /// Refreshes which slot owns the foreground window. Called from the bar's existing tick rather
    /// than a timer of its own; Windows does not push foreground changes to us and ManagedShell's
    /// own tracking is not reliable enough to drive the indicator.
    /// </summary>
    public void UpdateForeground(IntPtr foreground)
    {
        foreach (TaskbarSlot slot in Slots) slot.SetForeground(foreground);
    }

    public void Dispose()
    {
        if (_windows is INotifyCollectionChanged observable)
            observable.CollectionChanged -= OnWindowsChanged;

        _pins.Changed -= Rebuild;

        foreach (TaskbarSlot slot in Slots) slot.Detach();
        Slots.Clear();
    }

    private void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Dictionary<string, List<ApplicationWindow>> running = GroupRunningWindows();

        // Pinned applications hold their position whether or not anything is running.
        List<string> order = [.. _pins.Pins.Select(p => p.Key)];

        // Then everything else that is running, in the order the shell reports it.
        foreach (string key in running.Keys)
            if (!order.Any(k => AppIdentity.Same(k, key)))
                order.Add(key);

        for (int index = 0; index < order.Count; index++)
        {
            string key = order[index];
            PinnedApp? pin = _pins.Pins.FirstOrDefault(p => AppIdentity.Same(p.Key, key));
            List<ApplicationWindow> windows = Lookup(running, key);

            TaskbarSlot? existing = Slots.FirstOrDefault(s => AppIdentity.Same(s.Key, key));

            if (existing is null)
            {
                TaskbarSlot slot = new(key, pin);
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
