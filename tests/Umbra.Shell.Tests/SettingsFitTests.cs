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
            window.MaxHeight = 500;
            window.UpdateLayout();

            var root = (FrameworkElement)window.Content;
            double bottom = window.CloseButton.TransformToAncestor(root)
                .Transform(new Point(0, window.CloseButton.ActualHeight)).Y;

            Assert.True(root.ActualHeight < 500, $"content {root.ActualHeight} DIP in a 500 DIP window");
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
