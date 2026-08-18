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
/// Two kinds of setting, and the window is explicit about which is which. The resting icon size
/// and the motion layer are properties of what gets drawn, so they apply the moment they change.
/// The edge, the row alignment and the monitor are baked into the space-reserving AppBar
/// registration when the bar is constructed, and §6.3 forbids re-registering that rectangle in
/// steady state because it reflows every maximised window on the desktop. Those need a restart,
/// and the window says so rather than appearing to apply and quietly not.
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

        _working.Save();
        _applyLive(_working);

        UpdateRestartNote();
    }

    private void UpdateRestartNote()
    {
        bool needsRestart = _saved.NeedsRestartToReach(_working);

        RestartNote.Text = needsRestart
            ? "Saved. The edge, alignment, monitor or tray change takes effect when Umbra restarts."
            : string.Empty;

        RestartButton.IsEnabled = needsRestart;
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
