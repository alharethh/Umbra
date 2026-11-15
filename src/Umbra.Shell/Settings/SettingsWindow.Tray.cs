using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Settings;

/// <summary>
/// The Notification area page's list of icons, each with a "Show on bar" box. See
/// <see cref="TrayChoice"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not part of the settings this window saves.</b> The list is changed from the bar as well, by
/// dragging and from the chevron's menu, so it lives in the one <see cref="TrayChoice"/> the whole
/// process shares, and this page writes to that directly. It follows a change made on the bar while
/// the window is open.
/// </para>
/// <para>
/// A change made here does not rebuild the list, so the box just clicked keeps the keyboard focus.
/// </para>
/// </remarks>
internal partial class SettingsWindow
{
    private TrayChoice? _trayChoice;
    private Func<IReadOnlyList<TrayIconRow>>? _trayRows;
    private Func<IReadOnlyList<TrayEntry>>? _trayDrawn;

    /// <summary>True while this page is the one changing the choice, so it does not rebuild itself.</summary>
    private bool _changingTray;

    /// <summary>
    /// True while this page sets a box itself to match the choice. Unticking "show every icon"
    /// puts every icon back, so a box set to match must never be read as the user's click: a drop
    /// on the chevron that ends "show all" would otherwise empty the bar a moment later.
    /// </summary>
    private bool _syncingTray;

    /// <summary>
    /// Hands the page the shared choice and a way to list the icons the bar holds.
    /// </summary>
    /// <param name="rows">Null when the notification area is not hosted: there is nothing to list.</param>
    /// <param name="drawn">The bar's row as drawn, which unticking an icon under "show all" keeps.</param>
    internal void AttachTrayChoice(
        TrayChoice choice,
        Func<IReadOnlyList<TrayIconRow>>? rows,
        Func<IReadOnlyList<TrayEntry>>? drawn = null)
    {
        _trayChoice = choice;
        _trayRows = rows;
        _trayDrawn = drawn;

        SyncShowAllBox();
        ShowAllTrayBox.Checked += (_, _) => OnShowAllBox(true);
        ShowAllTrayBox.Unchecked += (_, _) => OnShowAllBox(false);

        choice.Changed += OnTrayChoiceChanged;
        Closed += (_, _) => choice.Changed -= OnTrayChoiceChanged;

        // Icons arrive for some seconds after the bar starts, so the list is read again whenever
        // the page is shown rather than once.
        TrayPage.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) RebuildTrayList();
        };

        RebuildTrayList();
    }

    /// <summary>
    /// The user ticked or unticked "show every icon": the same two actions as the chevron's menu.
    /// Every box in the list changes with it, so the list is built again.
    /// </summary>
    private void OnShowAllBox(bool showAll)
    {
        if (_syncingTray || _trayChoice is null) return;

        _changingTray = true;
        try
        {
            if (showAll) _trayChoice.ShowAllOnBar();
            else _trayChoice.PutAllBack();
        }
        finally { _changingTray = false; }

        RebuildTrayList();
    }

    private void SyncShowAllBox()
    {
        if (_trayChoice is null) return;

        _syncingTray = true;
        try { ShowAllTrayBox.IsChecked = _trayChoice.ShowAll; }
        finally { _syncingTray = false; }
    }

    private void OnTrayChoiceChanged()
    {
        if (_changingTray || _trayChoice is null) return;

        SyncShowAllBox();
        RebuildTrayList();
    }

    private void RebuildTrayList()
    {
        TrayIconList.Children.Clear();

        if (_trayChoice is null || _trayRows is null)
        {
            ShowAllTrayBox.IsEnabled = false;
            TrayChoiceHint.Text = "Turn on the notification area to choose which icons sit on the bar.";
            return;
        }

        IReadOnlyList<TrayIconRow> rows = _trayRows();

        if (rows.Count == 0)
        {
            TrayIconList.Children.Add(new TextBlock
            {
                Text = "No notification icons yet.",
                Style = (Style)FindResource("Hint"),
            });
        }

        foreach (TrayIconRow row in rows) TrayIconList.Children.Add(RowFor(row));
    }

    private CheckBox RowFor(TrayIconRow row)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };

        if (row.Icon is ImageSource icon)
        {
            content.Children.Add(new Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // The first line only: a tooltip like OneDrive's carries a second line of status, which
        // made rows of two heights. The whole of it is the box's tooltip.
        content.Children.Add(new TextBlock
        {
            Text = row.Name.Split('\n')[0].Trim(),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        CheckBox box = new()
        {
            Content = content,
            IsChecked = row.OnBar,
            Margin = new Thickness(0, 3, 0, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = row.IsRunning ? row.Name : "Not running. Untick to take it off the bar.",
        };

        System.Windows.Automation.AutomationProperties.SetName(box, $"Show {row.Name} on the bar");

        box.Checked += (_, _) => Choose(row, onBar: true);
        box.Unchecked += (_, _) => Choose(row, onBar: false);
        return box;
    }

    private void Choose(TrayIconRow row, bool onBar)
    {
        if (_trayChoice is null) return;

        _changingTray = true;
        try
        {
            // Unticking under "show all" is a drop on the chevron: that icon goes, "show all" ends,
            // the rest stay where they are drawn.
            if (onBar) _trayChoice.Add(row.Identifier, row.Matches);
            else _trayChoice.PutBehindChevron(row.Matches, _trayDrawn?.Invoke() ?? []);
        }
        finally { _changingTray = false; }

        // The list is not rebuilt, so the box just clicked keeps its focus; the switch above it
        // may have just ended, and says so.
        SyncShowAllBox();
    }
}
