using System;
using System.Windows.Input;
using Umbra.Shell.Rendering;
using Xunit;

namespace Umbra.Shell.Tests;

/// <summary>
/// When a click on a notification icon closes the overflow flyout. The case that broke: a left
/// click on an icon inside the flyout closed it at the button-up, so the second press of a
/// double-click landed on whatever was underneath and the application never saw a double-click.
/// </summary>
public class TrayOverflowClickCloseTests
{
    private const int DoubleClick = 500;

    [Fact]
    public void A_First_Left_Click_Inside_The_Flyout_Waits_Out_The_Double_Click_Time()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(DoubleClick),
            TrayOverflowClickClose.After(MouseButton.Left, iconIsInFlyout: true, completesDoubleClick: false, DoubleClick));
    }

    [Fact]
    public void The_Click_That_Completes_A_Double_Click_Closes_At_Once()
    {
        Assert.Equal(
            TimeSpan.Zero,
            TrayOverflowClickClose.After(MouseButton.Left, iconIsInFlyout: true, completesDoubleClick: true, DoubleClick));
    }

    [Fact]
    public void A_Left_Click_On_An_Icon_On_The_Bar_Closes_At_Once()
    {
        // Closing the flyout does not detach an icon that is not inside it.
        Assert.Equal(
            TimeSpan.Zero,
            TrayOverflowClickClose.After(MouseButton.Left, iconIsInFlyout: false, completesDoubleClick: false, DoubleClick));
    }

    [Theory]
    [InlineData(MouseButton.Right, true)]
    [InlineData(MouseButton.Right, false)]
    [InlineData(MouseButton.Middle, true)]
    public void Other_Buttons_Leave_It_Open(MouseButton button, bool inFlyout)
    {
        Assert.Null(TrayOverflowClickClose.After(button, inFlyout, completesDoubleClick: false, DoubleClick));
    }

    [Fact]
    public void A_Second_Press_On_The_Same_Icon_In_Time_Completes_A_Double_Click()
    {
        object icon = new();
        Assert.True(TrayOverflowClickClose.CompletesDoubleClick(icon, 1_000, icon, 1_000 + DoubleClick, DoubleClick));
        Assert.True(TrayOverflowClickClose.CompletesDoubleClick(icon, 1_000, icon, 1_060, DoubleClick));
    }

    [Fact]
    public void A_Late_Press_Or_Another_Icon_Or_No_Previous_Click_Does_Not()
    {
        object icon = new();
        object other = new();
        Assert.False(TrayOverflowClickClose.CompletesDoubleClick(icon, 1_000, icon, 1_001 + DoubleClick, DoubleClick));
        Assert.False(TrayOverflowClickClose.CompletesDoubleClick(other, 1_000, icon, 1_060, DoubleClick));
        Assert.False(TrayOverflowClickClose.CompletesDoubleClick(null, 0, icon, 60, DoubleClick));
    }
}
