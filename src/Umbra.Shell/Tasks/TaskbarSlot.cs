using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;
using Umbra.Shell.Rendering;

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
    private readonly AppIconResolver _icons;
    private readonly PackagedAppNames _packagedNames;
    private readonly Dispatcher _dispatcher;
    private ImageSource? _icon;
    private AppShortcut? _shortcut;
    private bool _shortcutChecked;
    private readonly AttentionLatch _attention = new();
    private DispatcherTimer? _decayTimer;

    public TaskbarSlot(
        string key,
        PinnedApp? pin,
        ShortcutResolver? shortcuts = null,
        AppIconResolver? icons = null,
        PackagedAppNames? packagedNames = null)
    {
        Key = key;
        Pin = pin;
        _shortcuts = shortcuts ?? new ShortcutResolver();
        _icons = icons ?? new AppIconResolver();
        _packagedNames = packagedNames ?? PackagedAppNames.Shared;

        // Captured here rather than reached for later: the resolver calls back from a worker, and
        // Dispatcher.CurrentDispatcher on that worker would create a second one that nothing pumps.
        _dispatcher = Dispatcher.CurrentDispatcher;
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
        UpdateAttention();
        return true;
    }

    /// <summary>
    /// Whether this application is asking for attention, and how loudly (§3.3). See
    /// <see cref="AttentionLatch"/> for the rule.
    /// </summary>
    public AttentionState Attention { get; private set; }

    /// <summary>Re-reads <see cref="Attention"/> from the windows' state.</summary>
    public void UpdateAttention()
    {
        bool flashing = _windows.Any(w => w.State == ApplicationWindow.WindowState.Flashing);

        // The setting is read only while something is asking, so a quiet bar never touches it.
        bool enabled = !flashing || Umbra.Shell.Shell.TaskbarFlashingSetting.Enabled();
        double now = Environment.TickCount64 / 1000.0;

        AttentionState state = _attention.Update(flashing, IsActive, enabled, now);
        ScheduleDecay(state == AttentionState.Pulsing && _attention.DecaysAt is { } at ? at - now : null);

        if (state == Attention) return;

        Attention = state;
        Raise(nameof(Attention));
    }

    /// <summary>
    /// One wake-up, at the moment the badge steps down, and only while one is up. There is no
    /// standing timer for this (§6.6): a bar with nothing asking holds none.
    /// </summary>
    private void ScheduleDecay(double? seconds)
    {
        _decayTimer?.Stop();
        if (seconds is not { } s) return;

        if (_decayTimer is null)
        {
            _decayTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _decayTimer.Tick += OnDecayDue;
        }

        _decayTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.05, s + 0.05));
        _decayTimer.Start();
    }

    private void OnDecayDue(object? sender, EventArgs e)
    {
        _decayTimer?.Stop();
        UpdateAttention();
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
                string? file = window.WinFileName;

                if (PackagedAppNames.IsFrameHost(file))
                {
                    string? aumid = window.AppUserModelID;
                    _packagedNames.Request(aumid, OnPackagedNameResolved);

                    return NameFor(file, null, aumid, window.Title, _packagedNames.Lookup(aumid)) ?? Key;
                }

                if (NameFor(file, window.WinFileDescription, null, null, null) is { } name) return name;
            }

            return Key;
        }
    }

    /// <summary>
    /// A window's application name, from what is known about it. Pure, so it can be tested
    /// without a window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A window in <c>ApplicationFrameHost.exe</c> is never named after it.</b> Every UWP
    /// application is drawn inside that host, so its file description, "Application Frame Host",
    /// is the name of the frame and not of Settings or Calculator. The application's own name comes
    /// from its app user model ID, resolved on a worker by <see cref="PackagedAppNames"/>.
    /// </para>
    /// <para>
    /// Until that answers, the window title stands in. It is the one place this class uses a title
    /// for a name, and only because both alternatives are worse: the host's name is wrong for
    /// every packaged application at once, and the bare ID reads as gibberish. It lasts as long as
    /// the worker takes, and the title of a UWP frame was the application's name in every case
    /// measured.
    /// </para>
    /// </remarks>
    internal static string? NameFor(string? file, string? description, string? aumid, string? title, string? packagedName)
    {
        if (PackagedAppNames.IsFrameHost(file))
        {
            if (!string.IsNullOrWhiteSpace(packagedName)) return packagedName;
            if (!string.IsNullOrWhiteSpace(title)) return title;

            return string.IsNullOrWhiteSpace(aumid) ? null : aumid;
        }

        if (!string.IsNullOrWhiteSpace(description)) return description;
        if (!string.IsNullOrWhiteSpace(file)) return System.IO.Path.GetFileNameWithoutExtension(file);

        return null;
    }

    private void OnPackagedNameResolved()
    {
        if (_dispatcher.CheckAccess()) Raise(nameof(DisplayName));
        else _dispatcher.BeginInvoke(() => Raise(nameof(DisplayName)));
    }

    /// <summary>
    /// Whether this slot's windows are drawn inside the UWP frame host, so that its executable
    /// says nothing about the application.
    /// </summary>
    private bool IsFrameHosted => _windows.Any(w => PackagedAppNames.IsFrameHost(w.WinFileName));

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
    /// The icon — the <em>application's</em>, not any one window's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A window's own icon is the last resort here, and that inverts what this used to do.</b>
    /// Measured on the development machine: two File Explorer windows, one on Downloads and one
    /// on Videos, answer <c>WM_GETICON</c> with different icons, because Explorer advertises the
    /// folder rather than itself. This slot groups both windows, so it drew whichever happened to
    /// be first in ManagedShell's collection and changed when an unrelated window opened.
    /// <c>explorer.exe</c> has exactly one icon and that is the right thing to draw. See
    /// <see cref="AppIconResolver"/> for the rest of the reasoning and
    /// <see cref="AppIconResolver.PreferApplicationIcon"/> for the setting that restores Windows'
    /// behaviour.
    /// </para>
    /// <para>
    /// <b>Nothing in this getter touches the shell.</b> It is a dictionary lookup and a list read;
    /// the resolution it kicks off happens on a worker with a timeout. The old version called
    /// <c>GetImageFromAssociatedIcon</c> here, which never returns for at least one executable on
    /// this machine and froze the bar before its window was ever shown. That is also why the
    /// former "only a pinned slot may synthesise an icon" guard is gone: it existed solely to keep
    /// a blocking call off the critical path, and there is no longer a blocking call to keep off.
    /// </para>
    /// </remarks>
    public ImageSource? Icon
    {
        get
        {
            ImageSource? application = _icons.Lookup(Key);
            if (application is null) RequestApplicationIcon();

            ImageSource? live = _windows.Select(w => w.Icon).FirstOrDefault(i => i is not null);

            // A frame-hosted slot's executable is ApplicationFrameHost.exe, whose icon is the same
            // generic frame for every UWP application. ManagedShell reads those windows' icons out
            // of the package by app user model ID, so the window icon is the application's, and wins.
            ImageSource? chosen = _icons.PreferApplicationIcon && !IsFrameHosted
                ? application ?? live
                : live ?? application;

            // Remembered so that a pinned application whose last window just closed keeps the icon
            // it had, rather than blinking to the placeholder while the executable is re-read.
            if (chosen is not null) return _icon = chosen;
            if (_icon is not null) return _icon;

            // Only once the resolver has actually given up. Drawing the placeholder while a real
            // icon is still milliseconds away would flash a row of identical squares on every
            // start, which looks more broken than the momentary gap it replaces.
            return _icons.Exhausted(Key) ? PlaceholderIcon.Get() : null;
        }
    }

    /// <summary>Asks for this application's icon. Idempotent; returns immediately.</summary>
    /// <remarks>
    /// The two strings are read here, on the UI thread, and captured. Everything the closure does
    /// afterwards — including enumerating both Start Menus — happens on the resolver's worker.
    /// </remarks>
    private void RequestApplicationIcon()
    {
        string? executable = ExecutablePath;
        string? name = Pin?.Name ?? DisplayName;

        _icons.Request(Key, () => Candidates(executable, name), OnApplicationIconResolved);
    }

    /// <summary>Where this application's icon might be, best first. Runs on a worker.</summary>
    private IReadOnlyList<IconCandidate> Candidates(string? executable, string? name)
    {
        List<IconCandidate> candidates = [];

        // The executable a live window is actually running from, first: it beats a shortcut of the
        // same name that happens to point somewhere else.
        if (UsableIconSource(executable)) candidates.Add(new IconCandidate(executable!));

        // Then the Start Menu, which is what survives an application updating into a new versioned
        // directory — the Discord case ShortcutResolver exists for. Its declared icon location
        // comes first because that is frequently a dedicated .ico while the target is a launcher
        // stub with no artwork of its own.
        if (_shortcuts.Resolve(name, executable) is { } shortcut)
        {
            IconCandidate declared = ShellIconSource.Parse(shortcut.IconPath);

            if (!string.IsNullOrWhiteSpace(declared.Path)) candidates.Add(declared);
            if (!string.IsNullOrWhiteSpace(shortcut.Target)) candidates.Add(new IconCandidate(shortcut.Target));
        }

        return candidates;
    }

    private void OnApplicationIconResolved()
    {
        if (_dispatcher.CheckAccess()) Raise(nameof(Icon));
        else _dispatcher.BeginInvoke(() => Raise(nameof(Icon)));
    }

    /// <summary>Tells the view to re-read <see cref="Icon"/>.</summary>
    public void RaiseIconChanged() => Raise(nameof(Icon));

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
            // A packaged application is started by its app user model ID, which is this slot's key,
            // and the caller falls through to exactly that on null. Not even the Start Menu is
            // asked: a shortcut found by the name "Settings" could be anybody's.
            if (Pin is { IsPackaged: true }) return null;

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

    /// <summary>
    /// Whether an executable's own icon can stand for the application: it exists, and it is not
    /// the UWP frame host, whose icon is the same generic frame for every packaged application.
    /// </summary>
    internal static bool UsableIconSource(string? executable) =>
        PathIsUsable(executable) && !PackagedAppNames.IsFrameHost(executable);

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
        UpdateAttention();
        return true;
    }

    /// <summary>Detaches from its windows. Call when the slot leaves the bar.</summary>
    public void Detach()
    {
        foreach (ApplicationWindow window in _windows) window.PropertyChanged -= OnWindowChanged;
        _windows.Clear();
        _decayTimer?.Stop();
    }

    /// <summary>Tells the view the pin state changed.</summary>
    public void RaisePinChanged()
    {
        // The pin may have been healed to a path that works, so any shortcut fallback found for the
        // broken one is no longer the best answer — and neither is an icon read out of the path it
        // used to hold.
        _shortcutChecked = false;
        _shortcut = null;
        _icon = null;
        _icons.Invalidate(Key);

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
            case nameof(ApplicationWindow.State):
                UpdateAttention();
                break;
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(Windows));
        Raise(nameof(IsRunning));
        Raise(nameof(IsActive));
        Raise(nameof(Title));
        Raise(nameof(DisplayName));
        Raise(nameof(Icon));
    }

    /// <summary>
    /// What this slot is called. <b>Read aloud by screen readers, so it is not optional.</b>
    /// </summary>
    /// <remarks>
    /// WPF names a generated <c>DataItem</c> peer by calling <c>ToString()</c> on the bound
    /// object, and the default returns the type's full name. Measured through UI Automation
    /// before this existed: every icon on the bar announced as
    /// "Umbra.Shell.Tasks.TaskbarSlot" — the same words for all of them, which is worse for a
    /// screen reader than announcing nothing.
    /// </remarks>
    public override string ToString() => DisplayName;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
