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

    public SettingsWindow(UmbraSettings current, Action<UmbraSettings> applyLive, Action restart)
    {
        _saved = current;
        _working = current.Clone();
        _applyLive = applyLive;
        _restart = restart;

        InitializeComponent();

        PopulateEdges();
        PopulateAlignments();
        PopulateMonitors();
        PopulateHover();

        IconSize.Value = _working.DormantIconSize;
        MotionBox.IsChecked = _working.Motion;
        TrayBox.IsChecked = _working.Tray;

        UpdateIconSizeReadout();
        UpdateRestartNote();

        _loaded = true;

        // Wired after the initial values are in place, so populating the controls does not count
        // as the user changing anything.
        EdgeBox.SelectionChanged += (_, _) => Commit();
        AlignmentBox.SelectionChanged += (_, _) => Commit();
        MonitorBox.SelectionChanged += (_, _) => Commit();
        MotionBox.Checked += (_, _) => Commit();
        MotionBox.Unchecked += (_, _) => Commit();
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

    /// <summary>Copies the controls into the working settings, saves, and applies what can apply.</summary>
    private void Commit()
    {
        if (!_loaded) return;

        if (EdgeBox.SelectedItem is Choice { Value: BarEdge edge }) _working.Edge = edge;
        if (AlignmentBox.SelectedItem is Choice { Value: Alignment alignment }) _working.Alignment = alignment;
        if (MonitorBox.SelectedItem is Choice { Value: string monitor }) _working.Monitor = monitor;

        _working.DormantIconSize = Math.Round(IconSize.Value);
        _working.Motion = MotionBox.IsChecked == true;
        _working.Tray = TrayBox.IsChecked == true;

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
            ? "Saved. Hosting the notification area takes effect when Umbra restarts."
            : string.Empty;

        // Always available. Greying it out when nothing pending needed a restart meant the one
        // button that reliably fixes a misbehaving bar was unavailable exactly when it was wanted,
        // and the user has no other way to restart it — Umbra has no window to close.
        RestartButton.IsEnabled = true;
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        _restart();
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>One entry in a combo box: what the user reads, and what gets stored.</summary>
    private sealed record Choice(string Label, object Value)
    {
        public override string ToString() => Label;
    }
}
