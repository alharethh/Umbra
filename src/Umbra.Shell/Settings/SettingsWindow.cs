using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ManagedShell.AppBar;
using Umbra.Core.Layout;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Settings;

/// <summary>
/// The settings window. Everything here was previously a command-line flag or a text file.
/// </summary>
/// <remarks>
/// <para>
/// Everything here applies the moment it changes, except hosting the notification area — that is
/// built into the shell host at startup. The edge and the monitor re-register the space-reserving
/// AppBar, which §6.3 forbids doing *in steady state* because it reflows every maximised window on
/// the desktop; a user deliberately moving the bar is not steady state, and the reflow is the point.
/// </para>
/// <para>
/// The restart hands the new instance the old one's process id to wait on. Without that the new
/// bar can capture Explorer's taskbar state while the old one still has it suppressed, and then
/// faithfully "restore" the user's taskbar to hidden on its own exit — the silent failure
/// <c>ExplorerStateGuardTests</c> exists for.
/// </para>
/// </remarks>
internal partial class SettingsWindow : Window
{
    private readonly UmbraSettings _saved;
    private readonly UmbraSettings _working;
    private readonly Action<UmbraSettings> _applyLive;
    private readonly Action _restart;

    private bool _loaded;

    /// <param name="current">The settings as they stand now, which the controls start from.</param>
    /// <param name="applyLive">Called with the saved settings after every change.</param>
    /// <param name="restart">Restarts Umbra.</param>
    /// <param name="running">
    /// What the running bar was started with, which decides whether a change needs a restart.
    /// Not <paramref name="current"/>: once a restart-only setting has been changed and saved,
    /// a window opened later would otherwise compare against the saved value and stop saying a
    /// restart is needed, while the bar still runs the old one.
    /// </param>
    public SettingsWindow(UmbraSettings current, Action<UmbraSettings> applyLive, Action restart,
                          UmbraSettings? running = null)
    {
        _saved = running ?? current;
        _working = current.Clone();
        _applyLive = applyLive;
        _restart = restart;

        InitializeComponent();

        // Taller than some screens' work areas; the settings scroll and the footer stays put.
        WorkAreaFit.Attach(this);

        Nav.SelectionChanged += (_, _) => ShowPage(Nav.SelectedIndex);
        Nav.SelectedIndex = 0;

        PopulateEdges();
        PopulateAlignments();
        PopulateAway();
        PopulateClockFormats();
        PopulateMonitors();
        PopulateHover();

        IconSize.Value = _working.DormantIconSize;
        Brightness.Value = _working.RestingBrightness;
        MotionBox.IsChecked = _working.Motion;
        TrayBox.IsChecked = _working.Tray;
        EveryMonitorBox.IsChecked = _working.BarOnEveryMonitor;

        // <b>The registry is the truth here, not the settings file.</b> The sign-in entry lives
        // outside Umbra and Task Manager's Startup tab can switch it off without telling us, so
        // showing the stored value would leave the checkbox claiming something untrue. Reading
        // the key back means the box always says what Windows will actually do.
        _working.StartWithWindows = Shell.StartupRegistration.IsEnabled();
        StartupBox.IsChecked = _working.StartWithWindows;

        UpdateIconSizeReadout();
        UpdateBrightnessReadout();
        UpdateRestartNote();

        _loaded = true;

        // Wired after the initial values are in place, so populating the controls does not count
        // as the user changing anything.
        EdgeBox.SelectionChanged += (_, _) => Commit();
        AlignmentBox.SelectionChanged += (_, _) => Commit();
        AwayBox.SelectionChanged += (_, _) => OnAwayChosen();
        AwaySeconds.TextChanged += (_, _) => Commit();
        ClockFormatBox.SelectionChanged += (_, _) => Commit();
        MonitorBox.SelectionChanged += (_, _) => Commit();
        StartupBox.Checked += (_, _) => Commit();
        StartupBox.Unchecked += (_, _) => Commit();
        MotionBox.Checked += (_, _) => Commit();
        MotionBox.Unchecked += (_, _) => Commit();
        EveryMonitorBox.Checked += (_, _) => Commit();
        EveryMonitorBox.Unchecked += (_, _) => Commit();
        TrayBox.Checked += (_, _) => Commit();
        TrayBox.Unchecked += (_, _) => Commit();

        AccentBox.SelectionChanged += (_, _) => OnAccentChosen();
        AppHoverBox.SelectionChanged += (_, _) => Commit();

        // On every keystroke, but only acted on once the text is a colour — see ColourFrom. A
        // half-typed "#53E" is not an error to report, it is someone still typing.
        AccentHex.TextChanged += (_, _) => Commit();
        AppHoverHex.TextChanged += (_, _) => Commit();
    }

    /// <summary>
    /// The accent presets, and the row's two hover treatments.
    /// </summary>
    /// <remarks>
    /// Presets rather than a colour wheel, with a hex field beside them for anything else. A full
    /// picker is a lot of window for a choice most people make once, and the hex field is what
    /// makes the setting genuinely open rather than a list of six opinions.
    /// </remarks>
    private void PopulateHover()
    {
        AccentBox.Items.Add(new Choice("Cyan", UmbraSettings.DefaultHoverAccent));
        AccentBox.Items.Add(new Choice("Windows blue", "#4CC2FF"));
        AccentBox.Items.Add(new Choice("Violet", "#B69CFF"));
        AccentBox.Items.Add(new Choice("Green", "#6BE39A"));
        AccentBox.Items.Add(new Choice("Amber", "#FFC24C"));
        AccentBox.Items.Add(new Choice("Rose", "#FF8FA8"));
        AccentBox.Items.Add(new Choice("White", "#FFFFFF"));

        AccentBox.SelectedIndex = IndexOf(AccentBox, _working.HoverAccent);
        AccentHex.Text = _working.HoverAccent;

        AppHoverBox.Items.Add(new Choice("Plain chip", AppIconHover.Chip));
        AppHoverBox.Items.Add(new Choice("Holographic pane", AppIconHover.Holographic));
        AppHoverBox.SelectedIndex = IndexOf(AppHoverBox, _working.AppIconHover);

        AppHoverHex.Text = _working.AppIconHoverColour;

        UpdateSwatches();
    }

    /// <summary>
    /// Copies a chosen preset into the hex field, which is what the setting is actually read from.
    /// </summary>
    /// <remarks>
    /// One source of truth: the field. Reading the setting from whichever of the two was touched
    /// last would make the pair disagree the moment someone types a colour that happens to be a
    /// preset, and the preset list would then be lying about the current value.
    /// </remarks>
    private void OnAccentChosen()
    {
        if (!_loaded) return;
        if (AccentBox.SelectedItem is not Choice { Value: string hex }) return;
        if (string.Equals(hex, AccentHex.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return;

        AccentHex.Text = hex;
    }

    /// <summary>Shows each chosen colour beside its control, and keeps the preset list honest.</summary>
    /// <remarks>
    /// The preset selection follows the hex field rather than the other way round, so typing a
    /// colour that is not a preset clears the list instead of leaving it pointing at whatever was
    /// picked last. A list that names a colour the bar is not using is worse than an empty one.
    /// Setting the index re-enters through SelectionChanged, which stops at the first comparison
    /// because the text already is that preset.
    /// </remarks>
    private void UpdateSwatches()
    {
        AccentBox.SelectedIndex = IndexOf(AccentBox, AccentHex.Text.Trim());

        if (ColourFrom(AccentHex.Text) is { } accent)
            AccentSwatch.Background = new System.Windows.Media.SolidColorBrush(accent);

        if (ColourFrom(AppHoverHex.Text) is { } chip)
            AppHoverSwatch.Background = new System.Windows.Media.SolidColorBrush(chip);
    }

    /// <summary>
    /// A colour from text, or null while the text is not one yet.
    /// </summary>
    /// <remarks>
    /// <c>ColorConverter.ConvertFromString</c> throws on anything it does not recognise, and this
    /// runs on every keystroke — "#53E7F" is a normal thing to see on the way to "#53E7FF". Null
    /// means keep the last good value and say nothing.
    /// </remarks>
    private static System.Windows.Media.Color? ColourFrom(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length is not (7 or 9) || trimmed[0] != '#') return null;

        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(trimmed);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private void PopulateEdges()
    {
        foreach (BarEdge edge in Enum.GetValues<BarEdge>())
            EdgeBox.Items.Add(new Choice(edge.ToString(), edge));

        EdgeBox.SelectedIndex = Math.Max(0, IndexOf(EdgeBox, _working.Edge));
    }

    /// <summary>The pages, in the order the list on the left names them.</summary>
    internal IReadOnlyList<FrameworkElement> PageElements => [BarPage, WearPage, ClockPage, HoverPage, TrayPage];

    /// <summary>
    /// Shows one page and hides the rest.
    /// </summary>
    /// <remarks>
    /// Hidden, not Collapsed: a hidden page still takes its height in the shared cell, so the
    /// window stays the height of the tallest page whichever is open. Collapsing them would resize
    /// the window on every click in the list.
    /// </remarks>
    internal void ShowPage(int index)
    {
        if (index < 0) return;

        IReadOnlyList<FrameworkElement> pages = PageElements;
        for (int i = 0; i < pages.Count; i++)
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Hidden;

        if (Nav.SelectedIndex != index) Nav.SelectedIndex = index;
        Body.ScrollToTop();
    }

    /// <summary>The presets offered for "Black when away", in minutes.</summary>
    /// <remarks>
    /// Seconds as well as minutes since 0.7, asked for by a user who wanted it gone as soon as
    /// they looked away. The file has always stored fractional minutes, so nothing about it changes.
    /// </remarks>
    internal static readonly double[] AwayPresets = [0.0, 0.25, 0.5, 1.0, 5.0, 10.0, 30.0];

    /// <summary>
    /// The presets, plus whatever the file holds if it is none of those, so a hand-edited delay is
    /// shown as it is rather than overwritten by the first change made here.
    /// </summary>
    private void PopulateAway()
    {
        foreach (double minutes in AwayPresets)
            AwayBox.Items.Add(new Choice(AwayLabel(minutes), minutes));
        AwayBox.Items.Add(new Choice("Custom", CustomAway));

        int preset = IndexOf(AwayBox, _working.AwayAfterMinutes);
        if (preset >= 0)
        {
            AwayBox.SelectedIndex = preset;
        }
        else
        {
            AwaySeconds.Text = SecondsText(_working.AwayAfterMinutes);
            AwayBox.SelectedIndex = IndexOf(AwayBox, CustomAway);
        }

        AwayCustom.Visibility = IsCustomAway ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The value behind the "Custom" entry: the delay is in the seconds box instead.</summary>
    private static readonly object CustomAway = new();

    private bool IsCustomAway => AwayBox.SelectedItem is Choice { Value: var v } && ReferenceEquals(v, CustomAway);

    /// <summary>
    /// Shows the seconds box when "Custom" is picked, filled with the delay in force so switching
    /// to it changes nothing until the number is edited.
    /// </summary>
    private void OnAwayChosen()
    {
        if (IsCustomAway)
        {
            if (AwaySecondsFrom(AwaySeconds.Text) is null && _working.AwayAfterMinutes > 0)
                AwaySeconds.Text = SecondsText(_working.AwayAfterMinutes);

            AwayCustom.Visibility = Visibility.Visible;
            AwaySeconds.Focus();
            AwaySeconds.SelectAll();
        }
        else
        {
            AwayCustom.Visibility = Visibility.Collapsed;
        }

        Commit();
    }

    /// <summary>
    /// The delay the controls ask for, in minutes, or null while they ask for nothing usable yet:
    /// "Custom" with an empty or half-typed seconds box keeps the last good delay, as the hex
    /// fields do.
    /// </summary>
    internal double? ChosenAwayMinutes() =>
        IsCustomAway ? AwaySecondsFrom(AwaySeconds.Text)
        : AwayBox.SelectedItem is Choice { Value: double minutes } ? minutes
        : null;

    /// <summary>
    /// Minutes from a number of seconds typed by the user, or null if it is not one.
    /// </summary>
    /// <remarks>
    /// Whole or decimal, in the user's own decimal separator or a full stop. Zero is not a custom
    /// delay (Off is its own entry), and the file's one-day ceiling applies, so nothing typed here
    /// is a value the next launch would refuse to read back.
    /// </remarks>
    internal static double? AwaySecondsFrom(string text)
    {
        text = text.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            return null;

        if (!double.IsFinite(seconds) || seconds <= 0) return null;

        double minutes = seconds / 60.0;
        return minutes <= UmbraSettings.MaxAwayAfterMinutes ? minutes : null;
    }

    private static string SecondsText(double minutes) =>
        (minutes * 60.0).ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>"Off", "After 30 seconds", "After 1 minute", "After 10 minutes".</summary>
    /// <remarks>
    /// Under a minute reads in seconds, rounded to the second, because "After 0.25 minutes" is
    /// what a hand-edited file used to show and nobody thinks in quarter-minutes.
    /// </remarks>
    internal static string AwayLabel(double minutes)
    {
        if (minutes <= 0) return "Off";

        if (minutes < 1.0)
        {
            long seconds = Math.Max(1, (long)Math.Round(minutes * 60.0));
            return string.Create(CultureInfo.InvariantCulture, $"After {seconds} {(seconds == 1 ? "second" : "seconds")}");
        }

        return minutes == 1.0
            ? "After 1 minute"
            : string.Create(CultureInfo.InvariantCulture, $"After {minutes:0.##} minutes");
    }

    private void PopulateClockFormats()
    {
        ClockFormatBox.Items.Add(new Choice("Follow Windows", ClockFormat.Windows));
        ClockFormatBox.Items.Add(new Choice("24-hour (14:05)", ClockFormat.TwentyFourHour));
        ClockFormatBox.Items.Add(new Choice("12-hour (2:05 PM)", ClockFormat.TwelveHour));

        ClockFormatBox.SelectedIndex = Math.Max(0, IndexOf(ClockFormatBox, _working.ClockFormat));
    }

    private void PopulateAlignments()
    {
        AlignmentBox.Items.Add(new Choice("Left / bottom of the bar", Alignment.Start));
        AlignmentBox.Items.Add(new Choice("Centred", Alignment.Center));

        AlignmentBox.SelectedIndex = Math.Max(0, IndexOf(AlignmentBox, _working.Alignment));
    }

    /// <summary>
    /// Lists the attached monitors by device name.
    /// </summary>
    /// <remarks>
    /// Stored by name rather than index: indices are reassigned when a monitor is unplugged, so an
    /// index would silently move the bar to a different screen. A stored name that matches nothing
    /// falls back to the primary, which is also what a fresh install gets.
    /// </remarks>
    private void PopulateMonitors()
    {
        MonitorBox.Items.Add(new Choice("Primary monitor", string.Empty));

        try
        {
            foreach (AppBarScreen screen in AppBarScreen.FromAllScreens())
            {
                // One interpolated string, not a concatenation: string.Create's IFormatProvider
                // overload takes an interpolation handler, and concatenating collapses it to a
                // plain string that binds to the wrong overload entirely.
                string suffix = screen.Primary ? "  primary" : string.Empty;
                string label = string.Create(CultureInfo.InvariantCulture,
                    $"{screen.DeviceName}  ({screen.Bounds.Width}x{screen.Bounds.Height}){suffix}");

                MonitorBox.Items.Add(new Choice(label, screen.DeviceName));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            BringUpLog.Write(e, "could not enumerate monitors");
        }

        int index = IndexOf(MonitorBox, _working.Monitor);
        MonitorBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private static int IndexOf(ComboBox box, object value)
    {
        for (int i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is Choice choice && Equals(choice.Value, value)) return i;

        return -1;
    }

    private void OnIconSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;

        UpdateIconSizeReadout();
        Commit();
    }

    /// <summary>
    /// Says what the chosen size actually buys, because the geometry is misleading.
    /// </summary>
    /// <remarks>
    /// The permanently-lit core vanishes at 19.29 px and buys nothing there — the hottest pixel is
    /// still lit 99.8% of the time. A user dragging this slider to 20 would otherwise reasonably
    /// conclude they had fixed the problem.
    /// </remarks>
    private void UpdateIconSizeReadout()
    {
        double size = Math.Round(IconSize.Value);

        string effect = size switch
        {
            >= 24 => "no change",
            >= 19 => "no gain",
            >= 18 => "1.04x cooler",
            >= 16 => "1.25x cooler",
            >= 14 => "1.63x cooler",
            _ => "2.32x cooler",
        };

        IconSizeReadout.Text = string.Create(CultureInfo.InvariantCulture, $"{size:F0} px · {effect}");
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;

        UpdateBrightnessReadout();
        Commit();
    }

    private void UpdateBrightnessReadout() =>
        BrightnessReadout.Text = BrightnessEffect((int)Math.Round(Brightness.Value));

    /// <summary>
    /// What a resting brightness buys, from the wear model rather than a table: wear goes as
    /// <c>L^n</c>, so the gain is the reciprocal of the light raised to the model's exponent.
    /// </summary>
    internal static string BrightnessEffect(int percent)
    {
        if (percent >= 100) return "100% · no change";

        double gain = Math.Pow(100.0 / percent, Umbra.Core.Wear.WearModel.DefaultExponent);
        return string.Create(CultureInfo.InvariantCulture, $"{percent}% · {gain:0.#}x cooler");
    }

    /// <summary>Copies the controls into the working settings, saves, and applies what can apply.</summary>
    private void Commit()
    {
        if (!_loaded) return;

        if (EdgeBox.SelectedItem is Choice { Value: BarEdge edge }) _working.Edge = edge;
        if (AlignmentBox.SelectedItem is Choice { Value: Alignment alignment }) _working.Alignment = alignment;
        if (MonitorBox.SelectedItem is Choice { Value: string monitor }) _working.Monitor = monitor;

        _working.DormantIconSize = Math.Round(IconSize.Value);
        _working.RestingBrightness = (int)Math.Round(Brightness.Value);
        _working.Motion = MotionBox.IsChecked == true;
        if (ChosenAwayMinutes() is { } away) _working.AwayAfterMinutes = away;
        if (ClockFormatBox.SelectedItem is Choice { Value: ClockFormat clockFormat }) _working.ClockFormat = clockFormat;
        _working.Tray = TrayBox.IsChecked == true;
        _working.BarOnEveryMonitor = EveryMonitorBox.IsChecked == true;
        _working.StartWithWindows = StartupBox.IsChecked == true;

        // Applied here rather than through _applyLive, which is the bar's drawn state. This
        // writes to the registry and has nothing to do with the window.
        Shell.StartupRegistration.Apply(_working.StartWithWindows);

        if (AppHoverBox.SelectedItem is Choice { Value: AppIconHover appHover }) _working.AppIconHover = appHover;

        // Only when the text is a colour. Half-typed values leave the last good one in place
        // rather than writing something the bar would have to fall back from.
        if (ColourFrom(AccentHex.Text) is not null) _working.HoverAccent = AccentHex.Text.Trim();
        if (ColourFrom(AppHoverHex.Text) is not null) _working.AppIconHoverColour = AppHoverHex.Text.Trim();

        UpdateSwatches();

        _working.Save();
        _applyLive(_working);

        UpdateRestartNote();
    }

    private void UpdateRestartNote()
    {
        bool needsRestart = _saved.NeedsRestartToReach(_working);

        RestartNote.Text = needsRestart
            ? "Saved. Hosting the notification area and a bar on every monitor take effect when Umbra restarts."
            : string.Empty;

        // Always available. Greying it out when nothing pending needed a restart meant the one
        // button that reliably fixes a misbehaving bar was unavailable exactly when it was wanted,
        // and the user has no other way to restart it — Umbra has no window to close.
        RestartButton.IsEnabled = true;
    }

    /// <summary>
    /// Wraps <paramref name="applyLive"/> so the caller keeps a copy of every save.
    /// </summary>
    /// <remarks>
    /// <b>The bug this exists for:</b> the window edits a clone, and the app used to keep only
    /// what it loaded at launch. Change a setting, close the window, open it again, and it showed
    /// the launch values; the next change then saved all of them back, undoing the first. The copy
    /// is a clone because the window goes on editing its own instance after handing it over.
    /// </remarks>
    internal static Action<UmbraSettings> Remembering(Action<UmbraSettings> remember, Action<UmbraSettings> applyLive) =>
        saved =>
        {
            remember(saved.Clone());
            applyLive(saved);
        };

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        _restart();
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Where the hint at the bottom of the window sends the user: the front page's section on
    /// getting the Windows taskbar back and removing Umbra. <c>RemovalHelpTests</c> checks the
    /// anchor against the README's headings, because a renamed heading breaks the link silently —
    /// GitHub just opens the top of the page.
    /// </summary>
    internal const string RemovalHelpUrl = "https://github.com/alharethh/Umbra#getting-your-windows-taskbar-back";

    /// <summary>
    /// A <c>Hyperlink</c> outside a navigation host does nothing on its own, so the browser is
    /// started here, through the shell, so it is the user's default one.
    /// </summary>
    private void OnRemovalHelpClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RemovalHelpUrl)
            {
                UseShellExecute = true,
            })?.Dispose();
            BringUpLog.Write($"settings: opened {RemovalHelpUrl}");
        }
        catch (Exception ex)
        {
            BringUpLog.Write(ex, $"settings: could not open {RemovalHelpUrl}");
        }
    }

    /// <summary>One entry in a combo box: what the user reads, and what gets stored.</summary>
    private sealed record Choice(string Label, object Value)
    {
        public override string ToString() => Label;
    }
}
