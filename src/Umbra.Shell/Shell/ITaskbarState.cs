using ManagedShell.AppBar;
using ManagedShell.Interop;

namespace Umbra.Shell.Shell;

/// <summary>
/// Explorer's taskbar state, behind an interface so <see cref="ExplorerStateGuard"/> can be tested
/// without a live shell.
/// </summary>
/// <remarks>
/// The abstraction earns its place: whether the guard restores correctly depends on the machine's
/// starting configuration, and on a machine whose taskbar already autohides the restore is a no-op
/// and proves nothing. The interesting case — autohide off, Umbra turns it on, Umbra must turn it
/// back off — cannot be exercised without changing the user's settings.
/// </remarks>
internal interface ITaskbarState
{
    /// <summary>The current AppBar state of Explorer's taskbar.</summary>
    NativeMethods.ABState GetState();

    /// <summary>Sets the AppBar state of Explorer's taskbar.</summary>
    void SetState(NativeMethods.ABState state);

    /// <summary>Whether Explorer's taskbar is being actively suppressed and re-hidden.</summary>
    bool Hidden { get; set; }

    /// <summary>
    /// Explorer's <c>TaskbarAl</c> setting — 0 for left, 1 for centre — or <see langword="null"/>
    /// when the value is absent and Windows is using its own default.
    /// </summary>
    /// <remarks>
    /// The Start menu positions itself from this and from where Explorer believes the taskbar to
    /// be, so §7.0 writes it to match Umbra's own alignment and restores it on exit. Absent is a
    /// distinct state from zero: putting a value back that was never there is still a change to the
    /// user's machine.
    /// </remarks>
    int? GetAlignment();

    /// <summary>Sets, or with <see langword="null"/> removes, <c>TaskbarAl</c>.</summary>
    void SetAlignment(int? alignment);

    /// <summary>
    /// The auto-hide setting Explorer has <i>saved</i> as the user's — the Settings toggle, not
    /// the runtime state — or <see langword="null"/> when it cannot be read in a known format.
    /// See <see cref="StuckRects"/>.
    /// </summary>
    bool? GetSavedAutoHide();

    /// <summary>
    /// Rewrites only the saved auto-hide bit. Does nothing when the saved settings are not in a
    /// format <see cref="StuckRects"/> recognises.
    /// </summary>
    void SetSavedAutoHide(bool autoHide);

    /// <summary>
    /// Identifies the Explorer process currently running the shell, or <see langword="null"/>
    /// when there is none. Two different answers in one run mean Explorer restarted.
    /// </summary>
    string? GetShellIdentity();

    /// <summary>
    /// Calls <paramref name="onChange"/> once straight away and again whenever Explorer's saved
    /// taskbar settings are written, until the result is disposed. Null when watching is not
    /// possible, which costs only the in-run repair; the one at exit still happens.
    /// </summary>
    /// <remarks>
    /// <paramref name="onChange"/> runs on a background thread. The call straight away is there
    /// because a write between deciding to watch and the watch starting would otherwise be missed.
    /// </remarks>
    IDisposable? WatchSavedSettings(Action onChange) => null;
}

/// <summary>The live implementation, over ManagedShell's <see cref="ExplorerHelper"/>.</summary>
internal sealed class ExplorerTaskbarState : ITaskbarState
{
    private readonly ExplorerHelper _explorerHelper;

    public ExplorerTaskbarState(ExplorerHelper explorerHelper)
    {
        ArgumentNullException.ThrowIfNull(explorerHelper);
        _explorerHelper = explorerHelper;
    }

    public NativeMethods.ABState GetState() => _explorerHelper.GetTaskbarState();

    public void SetState(NativeMethods.ABState state) => _explorerHelper.SetTaskbarState(state);

    public bool Hidden
    {
        get => _explorerHelper.HideExplorerTaskbar;
        set => _explorerHelper.HideExplorerTaskbar = value;
    }

    /// <summary>Where Explorer keeps the taskbar alignment (§7.0).</summary>
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const string AlignmentValue = "TaskbarAl";

    public int? GetAlignment()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return key?.GetValue(AlignmentValue) as int?;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void SetAlignment(int? alignment)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvancedKey, writable: true);

            if (key is null) return;

            if (alignment is null) key.DeleteValue(AlignmentValue, throwOnMissingValue: false);
            else key.SetValue(AlignmentValue, alignment.Value, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Not being able to move the Start menu is a cosmetic loss, not a reason to fail.
        }
    }

    public bool? GetSavedAutoHide()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StuckRects.KeyPath);
            return StuckRects.ReadAutoHide(key?.GetValue(StuckRects.ValueName) as byte[]);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    public void SetSavedAutoHide(bool autoHide)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StuckRects.KeyPath, writable: true);

            if (key is null) return;

            // Read and rewritten in one place so nothing but the one bit can change, and an
            // unfamiliar blob is left exactly as Explorer wrote it.
            byte[]? repaired = StuckRects.WithAutoHide(key.GetValue(StuckRects.ValueName) as byte[], autoHide);
            if (repaired is null) return;

            key.SetValue(StuckRects.ValueName, repaired, Microsoft.Win32.RegistryValueKind.Binary);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // A backstop that cannot run leaves things as they were without it.
        }
    }

    /// <remarks>
    /// The shell window is Explorer's desktop (<c>Progman</c>); Umbra creates no desktop of its own,
    /// so its owner is the shell Explorer. The process start time is part of the answer so a
    /// recycled process id cannot pass for the same Explorer.
    /// </remarks>
    public string? GetShellIdentity()
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) return null;

        GetWindowThreadProcessId(shell, out uint pid);
        if (pid == 0) return null;

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById((int)pid);
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                                 $"{pid}@{process.StartTime.ToFileTimeUtc()}");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <remarks>
    /// <c>RegNotifyChangeKeyValue</c> rather than a timer: Explorer's write is a single event at an
    /// unpredictable moment (+6.5 s and +7.6 s after a restart, measured), and a notification
    /// answers it within milliseconds while costing no wake-ups in between. The notification is
    /// one-shot, so it is re-armed <i>before</i> each check — armed after, a write landing between
    /// the two would go unseen. A dedicated thread, because an asynchronous registration is
    /// cancelled when the thread that made it exits, and a pool thread can exit at any moment.
    /// </remarks>
    public IDisposable? WatchSavedSettings(Action onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);

        Microsoft.Win32.RegistryKey? key;
        try
        {
            key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StuckRects.KeyPath);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }

        if (key is null) return null;

        SettingsWatch watch = new(key, onChange);
        watch.Start();
        return watch;
    }

    private sealed class SettingsWatch(Microsoft.Win32.RegistryKey key, Action onChange) : IDisposable
    {
        private readonly AutoResetEvent _changed = new(false);
        private readonly ManualResetEvent _stop = new(false);
        private Thread? _thread;
        private int _disposed;

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Umbra saved taskbar settings watch" };
            _thread.Start();
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    int armed = RegNotifyChangeKeyValue(
                        key.Handle, watchSubtree: false, RegNotifyChangeLastSet, _changed.SafeWaitHandle, asynchronous: true);

                    try
                    {
                        onChange();
                    }
                    catch (Exception e)
                    {
                        // A watch that dies takes the in-run repair with it, never the bar.
                        Diagnostics.BringUpLog.Write(e, "saved taskbar settings watch");
                    }

                    // Unarmed, a wait would never wake for a write; stop rather than spin.
                    if (armed != 0)
                    {
                        Diagnostics.BringUpLog.Write($"saved taskbar settings watch: RegNotifyChangeKeyValue failed ({armed})");
                        return;
                    }

                    if (WaitHandle.WaitAny([_stop, _changed]) == 0) return;
                }
            }
            catch (Exception e) when (e is ObjectDisposedException or System.IO.IOException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _stop.Set();

            // Bounded: the thread only ever blocks in WaitAny, which the stop event ends.
            if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(2));

            key.Dispose();
            _changed.Dispose();
            _stop.Dispose();
        }
    }

    private const uint RegNotifyChangeLastSet = 0x4;

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(
        Microsoft.Win32.SafeHandles.SafeRegistryHandle key,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        Microsoft.Win32.SafeHandles.SafeWaitHandle eventHandle,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool asynchronous);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
