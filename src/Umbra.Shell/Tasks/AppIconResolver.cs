using System.Collections.Concurrent;
using System.Windows.Media;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Tasks;

/// <summary>Somewhere an icon might be found: a file, and which icon within it.</summary>
/// <param name="Path">An executable, DLL or <c>.ico</c>.</param>
/// <param name="Index">
/// The icon's index within it. Negative values are resource IDs, which is how a shortcut's
/// <c>IconLocation</c> and <c>System.AppUserModel.RelaunchIconResource</c> both spell them.
/// </param>
internal readonly record struct IconCandidate(string Path, int Index = 0);

/// <summary>Extracts an icon from a file. Blocking, and permitted to never return.</summary>
/// <remarks>
/// An interface so the resolver's timeout, blacklist and retry behaviour can be tested without a
/// shell that has to be persuaded to hang. <see cref="ShellIconSource"/> is the real one.
/// </remarks>
internal interface IIconSource
{
    ImageSource? Extract(IconCandidate candidate);
}

/// <summary>
/// The icon for an <em>application</em>, as opposed to the icon for one of its windows.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because per-window icons are the wrong thing to draw on a taskbar slot, and
/// because reaching for one on the UI thread can freeze the bar.</b> Both of the icon faults this
/// replaced came out of the same design: resolution was per window, synchronous, single-attempt,
/// and on the UI thread.
/// </para>
/// <para>
/// <b>Per window was wrong.</b> Measured on the development machine: two File Explorer windows,
/// one showing Downloads and one showing Videos, answer <c>WM_GETICON</c> with <i>different</i>
/// icons — the folder's, not the application's. Windows 11 does the same thing deliberately and
/// offers no way to turn it off, so matching it is defensible under P7; what is not defensible is
/// that a slot grouping several windows drew whichever one happened to be first in ManagedShell's
/// collection, so the icon changed when an unrelated window opened. <c>explorer.exe</c> itself
/// carries one stable icon, and that is what a slot should show.
/// </para>
/// <para>
/// <b>Synchronous was worse.</b> The same measurement found <b>seven of ten</b> visible windows
/// returning nothing at all from <c>WM_GETICON</c> — every Electron, CEF, SDL and UWP application
/// on the machine. So for most modern software the whole pipeline already rested on reading the
/// executable, and that read is <c>SHGetFileInfo</c>, which on this machine <b>never returns</b>
/// for <c>C:\Program Files\Ascent\Ascent.exe</c>. It used to be called during the first layout
/// pass and froze the bar before its window was ever shown.
/// </para>
/// <para>
/// So: every extraction runs on a worker with a hard timeout, a candidate that times out is never
/// asked again this session, results are cached per candidate, and a failure that is <i>not</i> a
/// hang is retried a few times rather than being permanent. That last one is the fix for "the icon
/// is sometimes missing": ManagedShell tries the executable once, keeps whatever it had on
/// failure, and never looks again, so one unlucky read at startup lasted the session.
/// </para>
/// </remarks>
internal sealed class AppIconResolver
{
    /// <summary>
    /// How long one extraction may take before the candidate is abandoned.
    /// </summary>
    /// <remarks>
    /// Generous, because this never runs on the UI thread and a cold shell icon cache on a slow
    /// disk is genuinely slow. What it is defending against is not slowness but a call that will
    /// not come back at all.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How many times a candidate set may be tried before the slot gives up for the session.
    /// </summary>
    /// <remarks>
    /// Bounded rather than unlimited: the row rebuilds on every window event anywhere on the
    /// desktop, so an application whose icon genuinely cannot be read would otherwise retry
    /// forever, once per window that opens anywhere.
    /// </remarks>
    public const int DefaultMaxAttempts = 3;

    private readonly IIconSource _source;
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;

    private readonly ConcurrentDictionary<string, ImageSource> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Candidates that did not come back. Never asked again for the life of the process.
    /// </summary>
    /// <remarks>
    /// A timed-out extraction leaks the worker thread — there is no way to cancel a blocked
    /// cross-process shell call — so asking a second time costs a second thread for nothing.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _wedged = new(StringComparer.OrdinalIgnoreCase);

    public AppIconResolver(IIconSource? source = null, TimeSpan? timeout = null, int? maxAttempts = null)
    {
        _source = source ?? new ShellIconSource();
        _timeout = timeout ?? DefaultTimeout;
        _maxAttempts = maxAttempts ?? DefaultMaxAttempts;
    }

    /// <summary>How many candidates have been abandoned as unresponsive. Diagnostics only.</summary>
    public int WedgedCount => _wedged.Count;

    /// <summary>
    /// Whether a slot draws its application's icon in preference to its windows'.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives here rather than on the slot because every slot has to agree and the setting changes
    /// while the bar is running. On is the shipping default and is a deviation from Windows 11,
    /// recorded in design-spec §8: Windows shows File Explorer's <i>current folder</i> on the
    /// taskbar button and gives no way to turn that off, and a taskbar whose icons change as you
    /// work is a worse taskbar. Off restores Windows' behaviour for anyone who wants it.
    /// </para>
    /// <para>
    /// It does not change what is resolved, only which answer is preferred, so flipping it costs
    /// a repaint rather than a round of shell calls.
    /// </para>
    /// </remarks>
    public bool PreferApplicationIcon { get; set; } = true;

    /// <summary>
    /// The icon for this application if one is already known, without touching the shell.
    /// </summary>
    /// <remarks>
    /// <b>Always safe on the UI thread, and that is the point of it.</b> A dictionary read is what
    /// the layout pass is allowed to do; everything else happens in <see cref="Request"/>.
    /// </remarks>
    public ImageSource? Lookup(string key) =>
        _resolved.TryGetValue(key, out ImageSource? icon) ? icon : null;

    /// <summary>Whether this application has been given up on.</summary>
    public bool Exhausted(string key) =>
        !_resolved.ContainsKey(key) && _attempts.TryGetValue(key, out int n) && n >= _maxAttempts;

    /// <summary>
    /// Forgets what is known about one application, so the next request resolves it again.
    /// </summary>
    /// <remarks>
    /// Called when a pin is re-pointed at a path that works. The identity is unchanged, so the
    /// cache would otherwise keep serving an icon read from the executable that has since gone.
    /// The wedged list is deliberately <i>not</i> cleared: a path that failed to return once will
    /// do it again, and that is a property of the file rather than of the pin.
    /// </remarks>
    public void Invalidate(string key)
    {
        _resolved.TryRemove(key, out _);
        _attempts.TryRemove(key, out _);
    }

    /// <summary>
    /// Starts resolving this application's icon, if it is not already known or already running.
    /// </summary>
    /// <param name="key">The slot's identity, which is what the result is cached under.</param>
    /// <param name="candidates">
    /// Where to look, best first. <b>Evaluated on the worker, not here</b> — finding an
    /// application's Start Menu shortcut means enumerating both Start Menus, and doing that from
    /// the property getter this is called from would put disk I/O on the first layout pass. The
    /// caller must therefore capture anything it needs from UI-thread state before returning the
    /// closure, because the closure itself runs off-thread.
    /// </param>
    /// <param name="onResolved">Raised on a worker thread once, only when an icon was found.</param>
    /// <returns>True when a resolution was actually started.</returns>
    /// <remarks>
    /// Idempotent and cheap to call, because it is called from a property getter that the layout
    /// pass hits repeatedly. Two concurrent calls for the same key produce one resolution.
    /// </remarks>
    public bool Request(string key, Func<IReadOnlyList<IconCandidate>> candidates, Action onResolved)
    {
        if (_resolved.ContainsKey(key) || Exhausted(key)) return false;
        if (!_inFlight.TryAdd(key, 0)) return false;

        Task.Run(() => Resolve(key, candidates, onResolved));
        return true;
    }

    private void Resolve(string key, Func<IReadOnlyList<IconCandidate>> factory, Action onResolved)
    {
        try
        {
            IReadOnlyList<IconCandidate> candidates;

            try
            {
                candidates = factory();
            }
            catch (Exception e)
            {
                BringUpLog.Write(e, $"icon: building candidates for {key}");
                candidates = [];
            }

            foreach (IconCandidate candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.Path)) continue;
                if (_wedged.ContainsKey(Fingerprint(candidate))) continue;

                if (Extract(candidate) is not { } icon) continue;

                _resolved[key] = icon;
                _attempts.TryRemove(key, out _);
                onResolved();
                return;
            }

            // Nothing worked this time. Count it, and let the next rebuild try again — a cold icon
            // cache, a path mid-update or a shell still starting are all transient, and the old
            // behaviour of never looking twice is what made them permanent.
            int attempts = _attempts.AddOrUpdate(key, 1, (_, n) => n + 1);

            if (attempts >= _maxAttempts)
                BringUpLog.Write($"icon: giving up on {key} after {attempts} attempts");
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// One extraction, on a worker of its own, abandoned if it does not come back.
    /// </summary>
    /// <remarks>
    /// The nested <see cref="Task.Run(Action)"/> is not redundant. <see cref="Resolve"/> already
    /// runs off the UI thread, but it walks several candidates in turn — without a per-candidate
    /// timeout, one wedged path would strand that whole slot rather than costing it one entry.
    /// The stranded worker is never joined; there is no way to cancel a blocked LPC call, which is
    /// exactly why the candidate goes on <see cref="_wedged"/>.
    /// </remarks>
    private ImageSource? Extract(IconCandidate candidate)
    {
        Task<ImageSource?> work = Task.Run(() =>
        {
            try
            {
                return _source.Extract(candidate);
            }
            catch (Exception e)
            {
                BringUpLog.Write(e, $"icon: extracting {candidate.Path},{candidate.Index}");
                return null;
            }
        });

        if (!work.Wait(_timeout))
        {
            _wedged[Fingerprint(candidate)] = 0;
            BringUpLog.Write(
                $"icon: {candidate.Path} did not return within {_timeout.TotalSeconds:F0}s — abandoned for this session");
            return null;
        }

        ImageSource? icon = work.Result;

        // Frozen or it cannot cross to the UI thread, and an unfrozen ImageSource handed to WPF
        // from a worker throws on first use rather than where it was made.
        if (icon is { CanFreeze: true, IsFrozen: false }) icon.Freeze();

        return icon;
    }

    private static string Fingerprint(IconCandidate candidate) => $"{candidate.Path}|{candidate.Index}";
}
