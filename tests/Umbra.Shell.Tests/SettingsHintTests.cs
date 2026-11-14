using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using Umbra.Core.Layout;
using Umbra.Shell.Rendering;
using Umbra.Shell.Settings;
using Rect = System.Windows.Rect;

namespace Umbra.Shell.Tests;

/// <summary>
/// The one-time "right-click the bar for settings" note.
/// </summary>
public class SettingsHintTests
{
    private static readonly Size Note = new(220, 36);

    /// <summary>
    /// On every edge the note sits on the desktop side of the bar, a gap away, never over it, and
    /// centred along it. A sign error shows up on one edge only, so all four are checked.
    /// </summary>
    [Theory]
    [InlineData(BarEdge.Bottom)]
    [InlineData(BarEdge.Top)]
    [InlineData(BarEdge.Left)]
    [InlineData(BarEdge.Right)]
    public void The_Note_Sits_Beside_The_Bar_On_Its_Desktop_Side(BarEdge edge)
    {
        Rect screen = new(0, 0, 1504, 1003);
        Rect bar = edge switch
        {
            BarEdge.Bottom => new Rect(0, 955, 1504, 48),
            BarEdge.Top => new Rect(0, 0, 1504, 48),
            BarEdge.Left => new Rect(0, 0, 56, 1003),
            _ => new Rect(1448, 0, 56, 1003),
        };

        Point corner = SettingsHint.Placement(bar, edge, Note);
        Rect note = new(corner, Note);

        Assert.False(note.IntersectsWith(bar), $"the note {note} covers the bar {bar}");
        Assert.True(screen.Contains(note), $"the note {note} runs off the screen");

        double gap = edge switch
        {
            BarEdge.Bottom => bar.Top - note.Bottom,
            BarEdge.Top => note.Top - bar.Bottom,
            BarEdge.Left => note.Left - bar.Right,
            _ => bar.Left - note.Right,
        };
        Assert.Equal(SettingsHint.Gap, gap, 3);

        bool vertical = edge is BarEdge.Left or BarEdge.Right;
        double along = vertical ? note.Top + note.Height / 2 : note.Left + note.Width / 2;
        double centre = vertical ? bar.Top + bar.Height / 2 : bar.Left + bar.Width / 2;
        Assert.Equal(centre, along, 3);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]   // already shown: never again
    [InlineData(false, true, false, false)]   // over a fullscreen game: wait for a launch they will see
    [InlineData(false, false, true, false)]   // the bar has faded to black
    public void It_Shows_Only_Once_And_Only_Where_It_Can_Be_Seen(bool shown, bool fullScreen, bool away, bool expected) =>
        Assert.Equal(expected, SettingsHint.ShouldShow(shown, fullScreen, away));

    [Fact]
    public void The_Flag_Defaults_Off_And_Round_Trips()
    {
        Assert.False(new UmbraSettings().SettingsHintShown);
        Assert.True(new UmbraSettings { SettingsHintShown = true }.Clone().SettingsHintShown);

        string path = Path.Combine(Path.GetTempPath(), $"umbra-hint-{Guid.NewGuid():N}.txt");
        try
        {
            new UmbraSettings { SettingsHintShown = true }.Save(path);
            Assert.True(UmbraSettings.Load(path).SettingsHintShown);

            File.WriteAllLines(path, ["settingsHintShown = maybe"]);
            Assert.False(UmbraSettings.Load(path).SettingsHintShown);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_Click_On_The_Note_Closes_It() => OnSta(() =>
    {
        var hint = new SettingsHint();
        bool closed = false;
        hint.Closed += (_, _) => closed = true;

        hint.ShowBeside(new Rect(-30000, -30000, 1000, 48), BarEdge.Bottom);
        Assert.True(hint.IsVisible);

        hint.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseDownEvent,
        });

        Assert.True(closed, "a click on the note left it open");
    });

    [Fact]
    public void It_Never_Takes_The_Keyboard() => OnSta(() =>
    {
        var hint = new SettingsHint();
        try
        {
            hint.ShowBeside(new Rect(-30000, -30000, 1000, 48), BarEdge.Bottom);
            Assert.False(hint.ShowActivated);
            Assert.False(hint.IsActive);
            Assert.False(hint.ShowInTaskbar);
        }
        finally { hint.Close(); }
    });

    private static void OnSta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
