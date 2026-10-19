using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// Right-clicking Explorer's battery icon raised no menu, because it reports version 0 and was
/// sent only the button-up it ignores.
/// </summary>
/// <remarks>
/// The figures are the battery icon as the bar received it on the 150% laptop: owner class
/// <c>SystemTray_Main</c>, callback <c>0x4C9</c>, uid 1225, version 0; and the volume icon, an ATL
/// window with no callback message at all.
/// </remarks>
public class TrayRightClickTests
{
    private const uint BatteryCallback = 0x4C9;

    [Fact]
    public void The_Battery_Icon_Is_Sent_A_Context_Menu() =>
        Assert.True(TrayRightClick.NeedsContextMenu(0, "SystemTray_Main", BatteryCallback));

    [Fact]
    public void An_Icon_That_Declared_Version_4_Is_Not_Sent_A_Second_One()
    {
        // ManagedShell already sends WM_CONTEXTMENU from version 3 up; a second raises the menu twice.
        Assert.False(TrayRightClick.NeedsContextMenu(4, "SystemTray_Main", BatteryCallback));
        Assert.False(TrayRightClick.NeedsContextMenu(3, "SystemTray_Main", BatteryCallback));
    }

    [Theory]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("SystemTray_Main_")]
    [InlineData("systemtray_main")]
    [InlineData("")]
    [InlineData(null)]
    public void An_Ordinary_Version_0_Application_Is_Left_Alone(string? ownerClass) =>
        Assert.False(TrayRightClick.NeedsContextMenu(0, ownerClass, BatteryCallback));

    [Fact]
    public void An_Owner_With_No_Callback_Message_Is_Sent_Nothing() =>
        // The volume icon's shape. Message 0 is WM_NULL: sending it would report success and do nothing.
        Assert.False(TrayRightClick.NeedsContextMenu(0, "SystemTray_Main", 0));

    [Fact]
    public void The_Message_Is_Packed_As_Version_4()
    {
        // Cursor at physical (2047, 1468), the battery icon's centre on the laptop.
        uint cursor = (1468u << 16) | 2047u;

        (nint wParam, nint lParam) = TrayRightClick.ContextMenuMessage(cursor, 1225);

        // The anchor travels in wParam. In the version-0 packing (uid in wParam) Explorer drew the
        // menu at x = 1225, y = 0 — the top of the screen — which is how the packing was identified.
        Assert.Equal((nint)cursor, wParam);
        Assert.Equal(0x007B, (int)(lParam & 0xFFFF));
        Assert.Equal(1225, (int)((lParam >> 16) & 0xFFFF));
    }
}
