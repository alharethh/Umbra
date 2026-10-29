using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Umbra.Shell.Rendering;

namespace Umbra.Shell.Tests;

/// <summary>
/// Closing the tooltips the bar has up, which the away blackout relies on (backlog F5b).
/// </summary>
/// <remarks>
/// Real tooltips in real popup windows, opened against a real window placed off-screen: the
/// failure being guarded is that a tooltip is a separate window that nothing done to the bar's
/// content reaches, and only a real one shows whether the search finds it.
/// </remarks>
public sealed class OpenToolTipsTests
{
    [Fact]
    public void An_Open_ToolTip_Is_Closed()
    {
        OnSta(() =>
        {
            var (window, target) = Host();
            try
            {
                var tip = new ToolTip { PlacementTarget = target, Content = "Show desktop" };
                tip.IsOpen = true;
                Pump();
                Assert.True(tip.IsOpen);

                int closed = OpenToolTips.CloseAll();
                Pump();

                Assert.Equal(1, closed);
                Assert.False(tip.IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void The_Excepted_ToolTip_Stays_And_The_Others_Close()
    {
        OnSta(() =>
        {
            var (window, target) = Host();
            try
            {
                var stock = new ToolTip { PlacementTarget = target, Content = "Speakers: 26%" };
                var level = new ToolTip { PlacementTarget = target, Content = "Volume 28%" };
                stock.IsOpen = true;
                level.IsOpen = true;
                Pump();

                OpenToolTips.CloseAll(except: level);
                Pump();

                Assert.False(stock.IsOpen);
                Assert.True(level.IsOpen);
                level.IsOpen = false;
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Nothing_Open_Closes_Nothing()
    {
        OnSta(() =>
        {
            var (window, _) = Host();
            try { Assert.Equal(0, OpenToolTips.CloseAll()); }
            finally { window.Close(); }
        });
    }

    private static (Window, FrameworkElement) Host()
    {
        var target = new Border { Width = 40, Height = 40 };
        var window = new Window
        {
            Left = -10000, Top = -10000, Width = 100, Height = 100,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
            Content = target,
        };
        window.Show();
        Pump();
        return (window, target);
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
