using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Rendering;

namespace Umbra.Shell;

/// <summary>The one-time settings note. See <see cref="SettingsHint"/>.</summary>
internal partial class BarWindow
{
    /// <summary>How long after startup the note appears: after the bar has settled and drawn.</summary>
    internal static readonly TimeSpan SettingsHintDelay = TimeSpan.FromSeconds(3);

    private SettingsHint? _settingsHint;

    /// <summary>
    /// Shows the settings note once, if it has never been shown and the user can see the bar.
    /// </summary>
    /// <param name="alreadyShown">The stored flag.</param>
    /// <param name="markShown">Records the note as shown. Called the moment it appears.</param>
    public void OfferSettingsHint(bool alreadyShown, Action markShown)
    {
        if (alreadyShown) return;

        var delay = new DispatcherTimer { Interval = SettingsHintDelay };
        delay.Tick += (_, _) =>
        {
            delay.Stop();

            if (!SettingsHint.ShouldShow(alreadyShown, fullScreenInFront: _concededTo is not null, away: _away.IsAway))
            {
                BringUpLog.Write("settings hint: not shown this launch, the bar is out of sight");
                return;
            }

            _settingsHint = new SettingsHint();
            _settingsHint.Closed += (_, _) =>
            {
                PreviewMouseDown -= DismissSettingsHint;
                _settingsHint = null;
            };
            PreviewMouseDown += DismissSettingsHint;

            _settingsHint.ShowBeside(new Rect(Left, Top, ActualWidth, ActualHeight), Metrics.Edge);
            markShown();
            BringUpLog.Write("settings hint: shown once; it will not appear again");
        };
        delay.Start();
    }

    /// <summary>Any press on the bar ends the note, including the right-click it is asking for.</summary>
    private void DismissSettingsHint(object sender, MouseButtonEventArgs e) => _settingsHint?.Close();
}
