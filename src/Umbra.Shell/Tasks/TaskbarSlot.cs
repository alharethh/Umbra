using System.ComponentModel;
using System.Windows.Media;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTasks;

namespace Umbra.Shell.Tasks;

/// <summary>
/// One position on the bar: an application, with however many of its windows are open.
/// </summary>
/// <remarks>
/// The bar used to bind straight to ManagedShell's window list, which meant one slot per window and
/// nothing at all for an application that was pinned but not running. A slot is per application, so
/// a pinned app keeps its place when its last window closes, and an app with three windows occupies
/// one position rather than three.
/// </remarks>
internal sealed class TaskbarSlot : INotifyPropertyChanged
{
    private readonly List<ApplicationWindow> _windows = [];
    private ImageSource? _icon;

    public TaskbarSlot(string key, PinnedApp? pin)
    {
        Key = key;
        Pin = pin;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The application's identity.</summary>
    public string Key { get; }

    /// <summary>The pin record, when this application is pinned.</summary>
    public PinnedApp? Pin { get; set; }

    /// <summary>Whether this application is pinned to the bar.</summary>
    public bool IsPinned => Pin is not null;

    /// <summary>Open windows belonging to this application.</summary>
    public IReadOnlyList<ApplicationWindow> Windows => _windows;

    /// <summary>Whether anything is running.</summary>
    public bool IsRunning => _windows.Count > 0;

    /// <summary>Whether one of this application's windows is the foreground window.</summary>
    public bool IsActive => _windows.Any(w => w.State == ApplicationWindow.WindowState.Active);

    /// <summary>What to show on hover.</summary>
    public string Title => _windows.Count switch
    {
        0 => Pin?.Name ?? Key,
        1 => _windows[0].Title ?? Pin?.Name ?? Key,
        _ => $"{Pin?.Name ?? _windows[0].Title} — {_windows.Count} windows",
    };

    /// <summary>
    /// The icon. Taken from a live window where there is one, because that is the icon the
    /// application chose to advertise; read from the executable otherwise, which is the only source
    /// available for a pinned application that is not running.
    /// </summary>
    public ImageSource? Icon
    {
        get
        {
            ImageSource? live = _windows.Select(w => w.Icon).FirstOrDefault(i => i is not null);
            if (live is not null) return live;

            if (_icon is not null) return _icon;
            if (string.IsNullOrWhiteSpace(Pin?.Path)) return null;

            _icon = IconImageConverter.GetImageFromAssociatedIcon(Pin.Path, IconSize.Large);
            return _icon;
        }
    }

    /// <summary>Replaces this slot's windows, reporting whether anything visible changed.</summary>
    public bool SetWindows(IReadOnlyList<ApplicationWindow> windows)
    {
        bool changed = windows.Count != _windows.Count || !windows.SequenceEqual(_windows);
        if (!changed) return false;

        foreach (ApplicationWindow window in _windows) window.PropertyChanged -= OnWindowChanged;

        _windows.Clear();
        _windows.AddRange(windows);

        foreach (ApplicationWindow window in _windows) window.PropertyChanged += OnWindowChanged;

        RaiseAll();
        return true;
    }

    /// <summary>Detaches from its windows. Call when the slot leaves the bar.</summary>
    public void Detach()
    {
        foreach (ApplicationWindow window in _windows) window.PropertyChanged -= OnWindowChanged;
        _windows.Clear();
    }

    /// <summary>Tells the view the pin state changed.</summary>
    public void RaisePinChanged()
    {
        Raise(nameof(Pin));
        Raise(nameof(IsPinned));
        Raise(nameof(Title));
        Raise(nameof(Icon));
    }

    private void OnWindowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // State drives the running-indicator length, Title the tooltip, Icon the artwork. Anything
        // else a window reports is not shown here.
        switch (e.PropertyName)
        {
            case nameof(ApplicationWindow.State):
                Raise(nameof(IsActive));
                break;
            case nameof(ApplicationWindow.Title):
                Raise(nameof(Title));
                break;
            case nameof(ApplicationWindow.Icon):
                Raise(nameof(Icon));
                break;
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(Windows));
        Raise(nameof(IsRunning));
        Raise(nameof(IsActive));
        Raise(nameof(Title));
        Raise(nameof(Icon));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
