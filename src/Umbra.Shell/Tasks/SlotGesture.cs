using System.Windows.Input;

namespace Umbra.Shell.Tasks;

/// <summary>Which clicks on an application icon start another copy of the application.</summary>
/// <remarks>
/// <para>
/// The Windows taskbar's own gestures: a middle click, or a left click with Shift held. Both do
/// exactly what the jump list's entry for the application itself does, running or not.
/// </para>
/// <para>
/// <b>Shift alone, not Shift with Ctrl.</b> Ctrl+Shift+click is Windows' gesture for starting the
/// application as administrator. Umbra has no elevated launch, and starting an ordinary copy in
/// answer to it would quietly give the user something other than what they asked for, so that
/// combination stays an ordinary click. Alt is excluded for the same reason: it is not a gesture
/// the user would expect to launch anything.
/// </para>
/// <para>
/// A middle click carries no such meaning with any modifier, so it launches whatever is held.
/// </para>
/// </remarks>
internal static class SlotGesture
{
    public static bool StartsNewInstance(MouseButton button, ModifierKeys modifiers) => button switch
    {
        MouseButton.Middle => true,
        MouseButton.Left => modifiers == ModifierKeys.Shift,
        _ => false,
    };
}
