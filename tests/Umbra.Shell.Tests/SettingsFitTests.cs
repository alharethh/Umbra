using System.Runtime.ExceptionServices;
using System.Windows;
using Umbra.Shell.Settings;

namespace Umbra.Shell.Tests;

/// <summary>
/// The settings window on a screen shorter than its content.
/// </summary>
/// <remarks>
/// <para>
/// The window sizes itself to its content and cannot be resized, so before this it simply ran off
/// the bottom of a short screen, taking Close and Restart with it. The window is built and shown
/// for real, off-screen, because the failure is in layout and nothing short of layout shows it.
/// </para>
/// <para>
/// The height is set by hand rather than read from the monitor so that the result does not depend
/// on the screen the suite happens to run on: a tall one would let a broken window pass.
/// </para>
/// </remarks>
public class SettingsFitTests
{
    [Fact]
    public void Close_Stays_In_The_Window_When_The_Content_Does_Not_Fit() => OnSta(() =>
    {
        SettingsWindow window = Shown();
        try
        {
            // Shorter than any page since the window became pages; 500 no longer overflowed.
            window.MaxHeight = 300;
            window.UpdateLayout();

            var root = (FrameworkElement)window.Content;
            double bottom = window.CloseButton.TransformToAncestor(root)
                .Transform(new Point(0, window.CloseButton.ActualHeight)).Y;

            Assert.True(root.ActualHeight < 300, $"content {root.ActualHeight} DIP in a 300 DIP window");
            Assert.True(bottom <= root.ActualHeight, $"Close ends at {bottom} of {root.ActualHeight} DIP");
            Assert.True(window.Body.ScrollableHeight > 0, "nothing to scroll, so the clipped settings are unreachable");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void A_Window_That_Fits_Does_Not_Scroll() => OnSta(() =>
    {
        SettingsWindow window = Shown();
        try
        {
            window.MaxHeight = double.PositiveInfinity;
            window.UpdateLayout();

            Assert.Equal(0, window.Body.ScrollableHeight);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Every page fits the shortest screen the window is known to run on, with room to spare:
    /// the 2256×1504 laptop at 150%, whose work area is 954 DIP. The single-column window this
    /// replaced had reached it, which is why the pages exist.
    /// </summary>
    [Fact]
    public void Every_Page_Fits_The_Laptop_With_Room_For_More() => OnSta(() =>
    {
        SettingsWindow window = Shown();
        try
        {
            window.MaxHeight = double.PositiveInfinity;
            window.UpdateLayout();

            Assert.True(window.ActualHeight < 954.667 * 0.75,
                $"the window is {window.ActualHeight:0} DIP of the laptop's 955; a page has grown too tall");
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Moving between pages never resizes the window. The pages share one cell and the hidden ones
    /// still take their height; collapsing them instead would make the window jump on every click.
    /// </summary>
    [Fact]
    public void The_Window_Is_One_Size_On_Every_Page_And_Shows_One_Page_At_A_Time() => OnSta(() =>
    {
        SettingsWindow window = Shown();
        try
        {
            window.MaxHeight = double.PositiveInfinity;
            window.UpdateLayout();
            double height = window.ActualHeight;

            for (int page = 0; page < window.PageElements.Count; page++)
            {
                window.ShowPage(page);
                window.UpdateLayout();

                Assert.Equal(height, window.ActualHeight, 3);
                Assert.Equal(page, window.Nav.SelectedIndex);
                Assert.Single(window.PageElements, p => p.Visibility == Visibility.Visible);
                Assert.Equal(Visibility.Visible, window.PageElements[page].Visibility);
                Assert.Equal(0, window.Body.ScrollableHeight);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void The_List_Names_A_Page_For_Every_Page() => OnSta(() =>
    {
        SettingsWindow window = Shown();
        try
        {
            Assert.Equal(window.PageElements.Count, window.Nav.Items.Count);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(0.0, "Off")]
    [InlineData(0.25, "After 15 seconds")]
    [InlineData(0.5, "After 30 seconds")]
    [InlineData(1.0 / 60.0, "After 1 second")]
    [InlineData(1.0, "After 1 minute")]
    [InlineData(10.0, "After 10 minutes")]
    [InlineData(2.5, "After 2.5 minutes")]
    public void Away_Delays_Read_In_Seconds_Under_A_Minute(double minutes, string label) =>
        Assert.Equal(label, SettingsWindow.AwayLabel(minutes));

    [Fact]
    public void Away_Offers_Seconds_As_Well_As_Minutes() =>
        Assert.Equal([0.0, 0.25, 0.5, 1.0, 5.0, 10.0, 30.0], SettingsWindow.AwayPresets);

    [Theory]
    [InlineData(1432, 144u, 954.667)] // the 2256×1504 panel at 150%, 48 DIP bar
    [InlineData(1380, 120u, 1104)]   // the 2560×1440 development panel at 125%, 48 DIP bar
    [InlineData(1040, 96u, 1040)]
    public void Work_Area_Is_Converted_At_The_Monitors_Own_Dpi(int physical, uint dpi, double dips) =>
        Assert.Equal(dips, WorkAreaFit.HeightInDips(physical, dpi), 3);

    private static SettingsWindow Shown()
    {
        var window = new SettingsWindow(new UmbraSettings(), _ => { }, () => { })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        return window;
    }

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
