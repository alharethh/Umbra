using System.Globalization;
using System.Runtime.InteropServices;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// The input language indicator — the <c>ENG</c> or <c>NOB</c> the Windows taskbar shows beside
/// its clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a notification icon and no amount of tray work would have produced it.</b> It is
/// the Text Services Framework input indicator, which Explorer draws itself; it never appears in
/// <c>TrayIcons</c>. So it is ours to draw, and the reporting side of it is three calls.
/// </para>
/// <para>
/// The layout is per-thread, and the one worth showing is the foreground window's — that is the
/// one the next keystroke goes to. Reading our own would show whatever the bar's UI thread last
/// had, which is to say the session default, for ever.
/// </para>
/// <para>
/// <b>Polled from the proximity tick, not from a timer of its own.</b> That tick already reads the
/// foreground window for the running indicator, so the indicator costs one extra call on a
/// wake-up that was happening anyway — §6.6 is about not adding standing wake-ups.
/// </para>
/// </remarks>
internal sealed class KeyboardLayoutWatcher
{
    private static readonly TimeSpan InstalledRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly List<IntPtr> _installed = [];
    private DateTime _refreshedAt = DateTime.MinValue;
    private IntPtr _layout = IntPtr.Zero;
    private bool _hadChoice;
    private bool _logged;

    /// <summary>The three-letter name of the foreground window's input language, or empty.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Whether there is anything to show.
    /// </summary>
    /// <remarks>
    /// One installed layout means the indicator can never say anything the user does not already
    /// know, and Windows hides its own for the same reason. On this bar it would also be a
    /// permanently lit patch of text conveying nothing, which §2.3's duty budget has no room for.
    /// </remarks>
    public bool HasChoice => _installed.Count > 1;

    /// <summary>Re-reads the foreground window's layout.</summary>
    /// <returns>True when the displayed name changed.</returns>
    public bool Poll(IntPtr foreground)
    {
        RefreshInstalled();

        bool changed = false;

        IntPtr layout = LayoutOf(foreground);
        if (layout != _layout)
        {
            _layout = layout;

            string name = NameFor(layout);
            if (name != Name)
            {
                Name = name;
                changed = true;
            }

            LogFirstReading();
        }

        // Separately, because adding or removing a language changes whether the indicator is drawn
        // at all without changing what it says.
        if (HasChoice != _hadChoice)
        {
            _hadChoice = HasChoice;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Records what the first reading found, once.
    /// </summary>
    /// <remarks>
    /// An indicator that is correctly hidden and one that is showing nothing because the layout
    /// could not be read look identical from outside the process.
    /// </remarks>
    private void LogFirstReading()
    {
        if (_logged) return;

        _logged = true;
        BringUpLog.Write(
            $"input language: {(Name.Length == 0 ? "(unreadable)" : Name)} | " +
            $"{_installed.Count} layout(s) installed");
    }

    /// <summary>
    /// Switches the foreground window to the next installed layout, as clicking the Windows
    /// indicator does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ActivateKeyboardLayout</c> is the obvious call and the wrong one: it changes the layout
    /// of the <i>calling</i> thread, which here is the bar's UI thread — a thread that never
    /// receives a keystroke. The switch has to happen in the window the user is typing into, so
    /// the request is posted to it.
    /// </para>
    /// <para>
    /// The next layout is named explicitly rather than asking for <c>INPUTLANGCHANGE_FORWARD</c>,
    /// because forward is relative to the target's own idea of the list, and the list we are
    /// drawing from is ours.
    /// </para>
    /// </remarks>
    public void CycleForeground(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return;

        RefreshInstalled();
        if (_installed.Count < 2) return;

        IntPtr next = NextAfter(LayoutOf(foreground), _installed);
        if (next == IntPtr.Zero) return;

        NativeMethods.PostMessage(
            foreground, NativeMethods.WmInputLangChangeRequest, IntPtr.Zero, next);
    }

    /// <summary>
    /// The three-letter language name for a layout handle, uppercased, or empty when it cannot be
    /// resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A layout handle carries the language identifier in its low word and the physical layout in
    /// its high word, so two different keyboards for one language — US and Dvorak, say — resolve
    /// to the same name. That is what Windows shows too: the indicator names the language, and the
    /// layout underneath it is a separate choice.
    /// </para>
    /// <para>
    /// <b>The three-letter ISO name, not the Windows one.</b> They disagree on exactly the
    /// languages this feature was asked for: Norwegian Bokmål is <c>NOR</c> to Windows' own
    /// three-letter naming and <c>NOB</c> by ISO, and <c>NOB</c> is what the taskbar shows.
    /// </para>
    /// </remarks>
    public static string NameFor(IntPtr layout)
    {
        int languageId = (int)(layout.ToInt64() & 0xFFFF);
        if (languageId == 0) return string.Empty;

        try
        {
            return CultureInfo.GetCultureInfo(languageId)
                .ThreeLetterISOLanguageName
                .ToUpperInvariant();
        }
        catch (CultureNotFoundException)
        {
            // Layouts exist whose low word is not a culture Windows will name — IME sub-languages
            // among them. Nothing to show is better than something wrong.
            return string.Empty;
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The layout after <paramref name="current"/> in the circular installed list.
    /// </summary>
    /// <remarks>
    /// A layout that is not in the list — one installed since the list was last read — takes the
    /// first entry rather than nothing, so the click always does something.
    /// </remarks>
    public static IntPtr NextAfter(IntPtr current, IReadOnlyList<IntPtr> installed)
    {
        if (installed.Count == 0) return IntPtr.Zero;

        for (int i = 0; i < installed.Count; i++)
        {
            if (installed[i] == current) return installed[(i + 1) % installed.Count];
        }

        return installed[0];
    }

    private static IntPtr LayoutOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return IntPtr.Zero;

        uint thread = NativeMethods.GetWindowThreadProcessId(window, out _);

        return thread == 0 ? IntPtr.Zero : NativeMethods.GetKeyboardLayout(thread);
    }

    /// <summary>
    /// Re-reads the user's input languages, at most once per <see cref="InstalledRefreshInterval"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list changes when the user adds or removes a language, which is rare enough that
    /// reading it on every tick would be pure waste — and it has to be re-read at all, because a
    /// language added after we started would otherwise never appear.
    /// </para>
    /// <para>
    /// <b><c>GetKeyboardLayoutList</c> is the obvious source and it answers a different
    /// question.</b> It reports every layout <i>loaded in the session</i>, which includes ones
    /// some other process asked for. Measured on the development machine: four handles — Norwegian
    /// Bokmål, English (UK), English (US), and a fourth pairing the US language with the Norwegian
    /// device — against the two the user actually installed. Cycling through that list shows
    /// <c>ENG</c> three times in a row, which reads as a control that has stopped working, and it
    /// can drop the user onto a US key mapping they never chose while the indicator still says
    /// <c>ENG</c>.
    /// </para>
    /// <para>
    /// The user's own list is <c>Preload</c>, which is what Windows' indicator walks, in the order
    /// the user put them in. Each value is a layout identifier rather than a handle;
    /// <c>LoadKeyboardLayout</c> turns one into the other, and the layouts are already loaded, so
    /// it is a lookup. <c>KLF_NOTELLSHELL</c> keeps it that way — without it, Windows broadcasts a
    /// shell notification for a layout nobody switched to.
    /// </para>
    /// </remarks>
    private void RefreshInstalled()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _refreshedAt < InstalledRefreshInterval) return;

        _refreshedAt = now;

        List<IntPtr> preloaded = PreloadedLayouts();
        if (preloaded.Count > 0)
        {
            _installed.Clear();
            _installed.AddRange(preloaded);
            return;
        }

        // Fallback: the session's loaded layouts. Wider than the user's list, but a list that is
        // too wide beats an indicator that never appears.
        int count = NativeMethods.GetKeyboardLayoutList(0, null);
        if (count <= 0) return;

        IntPtr[] handles = new IntPtr[count];
        count = NativeMethods.GetKeyboardLayoutList(count, handles);

        _installed.Clear();
        for (int i = 0; i < count; i++) _installed.Add(handles[i]);
    }

    /// <summary>
    /// The numbered value names of <c>Preload</c>, in the user's order.
    /// </summary>
    /// <remarks>
    /// Named "1", "2", "3" — which is the order the user put their languages in and the order
    /// Windows cycles through them. <b>Sorted numerically, not as text</b>: the registry returns
    /// value names in whatever order it likes, and as text "10" sorts before "2", which would
    /// silently reorder the cycle for anyone with ten or more languages. Names that are not
    /// numbers are not part of the list and are dropped.
    /// </remarks>
    public static IEnumerable<string> InPreloadOrder(IEnumerable<string> names) =>
        names
            .Where(name => int.TryParse(name, CultureInfo.InvariantCulture, out _))
            .OrderBy(name => int.Parse(name, CultureInfo.InvariantCulture));

    /// <summary>The user's input languages, in their order, or empty when they cannot be read.</summary>
    private static List<IntPtr> PreloadedLayouts()
    {
        List<IntPtr> layouts = [];

        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");

            if (key is null) return layouts;

            foreach (string name in InPreloadOrder(key.GetValueNames()))
            {
                if (key.GetValue(name) is not string identifier || identifier.Length == 0) continue;

                IntPtr layout = NativeMethods.LoadKeyboardLayout(
                    identifier, NativeMethods.KlfNoTellShell);

                if (layout != IntPtr.Zero && !layouts.Contains(layout)) layouts.Add(layout);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Nothing readable; the caller falls back to the session's loaded layouts.
        }

        return layouts;
    }

    private static class NativeMethods
    {
        internal const int WmInputLangChangeRequest = 0x0050;

        /// <summary>Load the layout without broadcasting a shell notification for it.</summary>
        internal const uint KlfNoTellShell = 0x0080;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr LoadKeyboardLayout(string identifier, uint flags);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetKeyboardLayout(uint thread);

        [DllImport("user32.dll")]
        internal static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? handles);

        [DllImport("user32.dll")]
        internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
