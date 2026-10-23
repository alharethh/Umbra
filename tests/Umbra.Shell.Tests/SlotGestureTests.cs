using System.Windows.Input;
using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// A middle click, or Shift with a left click, on an application icon starts another copy of it,
/// as on the Windows taskbar.
/// </summary>
/// <remarks>
/// The silent failures are the neighbours: a plain left click that launched would open a new
/// window on every click instead of activating the one already there, and a right click that
/// launched would start the application under its own jump list.
/// </remarks>
public class SlotGestureTests
{
    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public void A_Middle_Click_Starts_A_New_Instance_With_Any_Modifier(ModifierKeys modifiers) =>
        Assert.True(SlotGesture.StartsNewInstance(MouseButton.Middle, modifiers));

    [Fact]
    public void Shift_With_A_Left_Click_Starts_A_New_Instance() =>
        Assert.True(SlotGesture.StartsNewInstance(MouseButton.Left, ModifierKeys.Shift));

    [Fact]
    public void A_Plain_Left_Click_Does_Not() =>
        Assert.False(SlotGesture.StartsNewInstance(MouseButton.Left, ModifierKeys.None));

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Windows)]
    public void Other_Modifiers_On_A_Left_Click_Do_Not(ModifierKeys modifiers) =>
        Assert.False(SlotGesture.StartsNewInstance(MouseButton.Left, modifiers));

    // Windows' gesture for "run as administrator". Umbra cannot elevate, and an ordinary copy is
    // not what was asked for.
    [Fact]
    public void Control_Shift_With_A_Left_Click_Does_Not() =>
        Assert.False(SlotGesture.StartsNewInstance(MouseButton.Left, ModifierKeys.Control | ModifierKeys.Shift));

    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Shift)]
    public void The_Right_Button_Never_Does(ModifierKeys modifiers) =>
        Assert.False(SlotGesture.StartsNewInstance(MouseButton.Right, modifiers));

    [Theory]
    [InlineData(MouseButton.XButton1)]
    [InlineData(MouseButton.XButton2)]
    public void The_Side_Buttons_Do_Not(MouseButton button) =>
        Assert.False(SlotGesture.StartsNewInstance(button, ModifierKeys.None));
}
