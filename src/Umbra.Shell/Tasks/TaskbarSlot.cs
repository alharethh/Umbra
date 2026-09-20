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
    private readonly ShortcutResolver _shortcuts;
    private ImageSource? _icon;
    private AppShortcut? _shortcut;
    private bool _shortcutChecked;

    public TaskbarSlot(string key, PinnedApp? pin, ShortcutResolver? shortcuts = null)
    {
        Key = key;
        Pin = pin;
        _shortcuts = shortcuts ?? new ShortcutResolver();
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

    /// <summary>
    /// Whether one of this application's windows is the foreground window.
    /// </summary>
    /// <remarks>
    /// Driven from the real foreground window rather than ManagedShell's <c>State</c>. State is
    /// maintained from foreground-change events and goes stale — the same reason clicking a slot
    /// asks Windows directly. Updated by <see cref="SetForeground"/> from the bar's existing tick.
    /// </remarks>
    public bool IsActive { get; private set; }

    /// <summary>Recomputes <see cref="IsActive"/> against the current foreground window.</summary>
    /// <returns>True when the value changed.</returns>
    public bool SetForeground(IntPtr foreground)
    {
        bool active = _windows.Any(w => w.Handle == foreground);
        if (active == IsActive) return false;

        IsActive = active;
        Raise(nameof(IsActive));
        return true;
    }

    /// <summary>
    /// The application's own name, for the menu item that launches another copy of it.
    /// </summary>
    /// <remarks>
    /// Never the window title, which is per-document: "Close window" sitting under a menu headed
    /// with the name of the file you happen to have open reads as being about that file. The file
    /// description is the application's own name and is what a pin already stores.
    /// </remarks>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Pin?.Name)) return Pin.Name;

            foreach (ApplicationWindow window in _windows)
            {
                if (!string.IsNullOrWhiteSpace(window.WinFileDescription)) return window.WinFileDescription;

                if (!string.IsNullOrWhiteSpace(window.WinFileName))
                    return System.IO.Path.GetFileNameWithoutExtension(window.WinFileName);
            }

            return Key;
        }
    }

    /// <summary>
    /// The application's executable, where one is known.
    /// </summary>
    /// <remarks>
    /// Taken from a live window first and the pin record second, because the pin's copy is a
    /// snapshot: a packaged application that updates moves into a new versioned directory and the
    /// stored path goes stale, while the running window always reports where it actually is.
    /// </remarks>
    public string? ExecutablePath
    {
        get
        {
            foreach (ApplicationWindow window in _windows)
                if (!string.IsNullOrWhiteSpace(window.WinFileName)) return window.WinFileName;

            return string.IsNullOrWhiteSpace(Pin?.Path) ? null : Pin.Path;
        }
    }

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

            if (live is not null)
            {
                // Remember it. When the last window closes, this is what keeps a pinned application
                // visible: reading the icon back out of the executable does not always work — the
                // recorded path can be empty, or point at something that is not a plain exe — and
                // when it fails the slot renders as a gap rather than an icon.
                _icon = live;
                return live;
            }

            if (_icon is not null) return _icon;

            // <b>Only a pinned application gets an icon synthesised, and widening that hangs the
            // bar.</b> A pin is the one thing that stays on the row with no window to take an icon
            // from; a running application without one yet simply has not been given it by
            // ManagedShell, and will raise PropertyChanged when it is.
            //
            // Asking for any slot without a live icon looks harmless and is not:
            // GetImageFromAssociatedIcon is a synchronous shell call on the UI thread, and on this
            // machine it <i>never returns</i> for C:\Program Files\Ascent\Ascent.exe. The bar
            // froze before its window was ever shown -- no icons, no clock, no Start button, and
            // no error, because nothing had thrown. It only reproduced with the tray switched off,
            // which is pure timing: the tray's extra startup work delays the first layout long
            // enough for the live icon to arrive, so the fallback is never reached.
            if (!IsPinned) return null;

            // The recorded path first: it is the executable this application's own window was
            // running from, which beats a shortcut of the same name pointing somewhere else.
            if (PathIsUsable(Pin?.Path))
            {
                _icon = IconImageConverter.GetImageFromAssociatedIcon(Pin!.Path, IconSize.Large);
                if (_icon is not null) return _icon;
            }

            // The path has gone. Ask the Start Menu where the application went — see
            // ShortcutResolver for why that is the answer rather than a guess.
            if (Shortcut() is not { } shortcut) return null;

            string source = PathIsUsable(shortcut.IconPath) ? shortcut.IconPath : shortcut.Target;
            _icon = IconImageConverter.GetImageFromAssociatedIcon(source, IconSize.Large);

            return _icon;
        }
    }

    /// <summary>
    /// What to run when this slot is clicked and nothing is open, and what to pass it.
    /// </summary>
    /// <remarks>
    /// The pin's own path while it still exists. Once it does not — the application updated into a
    /// new versioned directory — the Start Menu shortcut, which is what Windows itself launches and
    /// is why its pins survive an update.
    /// </remarks>
    public (string Path, string Arguments)? LaunchTarget
    {
        get
        {
            if (PathIsUsable(Pin?.Path)) return (Pin!.Path, string.Empty);

            return Shortcut() is { } shortcut ? (shortcut.Target, shortcut.Arguments) : null;
        }
    }

    /// <summary>The Start Menu shortcut for this application, looked up at most once.</summary>
    private AppShortcut? Shortcut()
    {
        if (_shortcutChecked) return _shortcut;

        _shortcutChecked = true;
        _shortcut = _shortcuts.Resolve(Pin?.Name ?? DisplayName, Pin?.Path ?? ExecutablePath);

        return _shortcut;
    }

    private static bool PathIsUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            return System.IO.File.Exists(path);
        }
        catch (ArgumentException)
        {
            // Not path-shaped. An app user model ID need not be.
            return false;
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
        // The pin may have been healed to a path that works, so any shortcut fallback found for the
        // broken one is no longer the best answer.
        _shortcutChecked = false;
        _shortcut = null;
        _icon = null;

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
