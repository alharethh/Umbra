using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Umbra.Shell.Rendering;

/// <summary>
/// Closes a context menu when the user clicks outside it, which WPF will not do for a bar that
/// never takes focus.
/// </summary>
/// <remarks>
/// <para>
/// WPF dismisses a context menu by capturing the mouse for the menu's popup and watching for a
/// click elsewhere. That works when the owning window can be activated. The bar carries
/// <c>WS_EX_NOACTIVATE</c> — which is what makes click-to-minimise work at all — so the menu never
/// holds focus, the capture is not honoured across processes, and clicking in another application
/// leaves the menu sitting open. The only way to close it was to click back inside Umbra, which is
/// not a thing anyone would guess.
/// </para>
/// <para>
/// So the menu watches the pointer itself, exactly as the window picker does for the same reason.
/// A button going down anywhere outside the menu's own window closes it, as does Escape. The timer
/// runs only while a menu is open, so it adds nothing to the steady-state wake-up budget §6.6 is
/// careful about.
/// </para>
/// <para>
/// Both the cursor and the menu's rectangle are read in <b>physical</b> pixels here — from
/// <c>GetCursorPos</c> and <c>GetWindowRect</c> respectively — so they are directly comparable and
/// no DPI conversion is involved. Mixing one of these with a WPF logical coordinate is the bug this
/// arrangement avoids by never having a logical coordinate in the first place.
/// </para>
/// </remarks>
internal sealed class ContextMenuDismisser
{
    private readonly DispatcherTimer _timer;
    private ContextMenu? _menu;

    public ContextMenuDismisser()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };

        _timer.Tick += OnTick;
    }

    /// <summary>Starts watching a menu that has just opened.</summary>
    public void Watch(ContextMenu menu)
    {
        _menu = menu;
        _timer.Start();
    }

    /// <summary>Stops watching. Safe to call for a menu that was never watched.</summary>
    public void Release()
    {
        _timer.Stop();
        _menu = null;
    }

    /// <summary>
    /// Closes the menu being watched, if there is one.
    /// </summary>
    /// <remarks>
    /// For the case the pointer rules cannot see: another panel of the bar's opening over the
    /// menu. No click landed outside it — the click landed on Umbra — so nothing here would have
    /// fired. Closing raises <c>Closed</c>, which releases the watch through the class handler;
    /// releasing again costs nothing and keeps this correct if that handler ever goes away.
    /// </remarks>
    public void Close()
    {
        if (_menu is { IsOpen: true } menu) menu.IsOpen = false;

        Release();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_menu is null || !_menu.IsOpen)
        {
            Release();
            return;
        }

        if (Pressed(VkEscape))
        {
            _menu.IsOpen = false;
            return;
        }

        // Any button, including the middle one: they all mean "I am interacting with something
        // else now".
        if (!Pressed(VkLButton) && !Pressed(VkRButton) && !Pressed(VkMButton)) return;

        if (!GetCursorPos(out NativePoint cursor)) return;
        if (InsideMenu(cursor)) return;

        _menu.IsOpen = false;
    }

    /// <summary>Whether the cursor is over the menu's own popup window.</summary>
    private bool InsideMenu(NativePoint cursor)
    {
        if (_menu is null) return false;
        if (PresentationSource.FromVisual(_menu) is not HwndSource source) return false;
        if (!GetWindowRect(source.Handle, out NativeRect bounds)) return false;

        return cursor.X >= bounds.Left && cursor.X < bounds.Right
            && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
    }

    private static bool Pressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;
    private const int VkEscape = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
