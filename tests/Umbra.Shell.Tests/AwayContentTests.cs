using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Umbra.Shell.Rendering;

namespace Umbra.Shell.Tests;

/// <summary>
/// The bar's content during the away blackout: nothing hoverable while it fades, no tooltip left
/// once it is black (backlog F5c).
/// </summary>
/// <remarks>
/// A real window off-screen and a real tooltip: what failed is that hover keeps working through a
/// fading element and that a tooltip survives its owner going hidden, and neither can be shown
/// without WPF doing the hit-testing and the popup itself.
/// </remarks>
public sealed class AwayContentTests
{
    [Fact]
    public void While_It_Fades_The_Pointer_Is_Over_Nothing_Of_The_Bar()
    {
        OnSta(() =>
        {
            var (window, content, icon) = Host();
            try
            {
                Assert.Same(icon, HitAt(window));

                AwayContent.Darken(content);
                Pump();

                Assert.NotSame(icon, HitAt(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void A_ToolTip_That_Opened_During_The_Fade_Is_Closed_At_Black()
    {
        OnSta(() =>
        {
            var (window, content, icon) = Host();
            try
            {
                AwayContent.Darken(content);
                var tip = new ToolTip { PlacementTarget = icon, Content = "Speakers: 0%" };
                tip.IsOpen = true;
                Pump();

                int closed = AwayContent.Hide(content);
                Pump();

                Assert.Equal(1, closed);
                Assert.False(tip.IsOpen);
                Assert.Equal(Visibility.Hidden, content.Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Coming_Back_Makes_The_Content_Hoverable_Again()
    {
        OnSta(() =>
        {
            var (window, content, icon) = Host();
            try
            {
                AwayContent.Darken(content);
                AwayContent.Hide(content);
                AwayContent.Restore(content);
                Pump();

                Assert.Equal(Visibility.Visible, content.Visibility);
                Assert.Same(icon, HitAt(window));
            }
            finally { window.Close(); }
        });
    }

    // InputHitTest, which is what mouse input is routed by. VisualTreeHelper.HitTest ignores
    // IsHitTestVisible and finds the icon either way, which made the first draft of this test
    // pass on broken code.
    private static DependencyObject? HitAt(Window window) =>
        window.InputHitTest(new Point(20, 20)) as DependencyObject;

    private static (Window, Grid, Border) Host()
    {
        var icon = new Border { Width = 40, Height = 40, Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var content = new Grid { Children = { icon } };
        var window = new Window
        {
            Left = -10000, Top = -10000, Width = 100, Height = 100,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
            Content = content,
        };
        window.Show();
        Pump();
        return (window, content, icon);
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void OnSta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
