using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Umbra.Shell.Settings;

/// <summary>
/// Keeps a window that sizes itself to its content no taller than the work area of the monitor
/// it is on.
/// </summary>
/// <remarks>
/// <para>
/// <b>WPF does not do this on its own.</b> <c>SizeToContent="Height"</c> grows the window to
/// whatever its content asks for, and a window taller than the screen simply runs off the
/// bottom — with <c>ResizeMode="NoResize"</c> there is then no way to reach what is down there.
/// The settings window measured 1412 of 1432 physical px of work area on a 2256×1504 panel at
/// 150%, and its Close
/// button is the last thing in it.
/// </para>
/// <para>
/// The cap is <see cref="FrameworkElement.MaxHeight"/>, which WPF applies to the outer window
/// (caption included) and which <c>SizeToContent</c> respects, so the window then lays its
/// content out in the height that is left and the scrolling region takes up the difference.
/// </para>
/// <para>
/// <b>Which monitor is the cursor's at first, then the window's own.</b> Before the window is
/// placed it has no monitor worth asking about; <c>CenterScreen</c> centres on the monitor under
/// the cursor, so that is the one measured. Once it is placed — and every time it is dragged —
/// its own monitor is, because a second screen can have a much shorter work area than the first.
/// </para>
/// </remarks>
internal static class WorkAreaFit
{
    /// <summary>
    /// Caps <paramref name="window"/> to its monitor's work area now and whenever it moves.
    /// </summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => Fit(window, MonitorUnderCursor());
        window.LocationChanged += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero) Fit(window, MonitorFromWindow(hwnd, MonitorDefaultToNearest));
        };
    }

    /// <summary>
    /// A work area's height in physical pixels, expressed in the DIP a window on that monitor
    /// lays out in.
    /// </summary>
    /// <remarks>
    /// Umbra is per-monitor-v2 aware, so a window's DIP are that monitor's effective DPI over 96,
    /// not the system DPI. Mixing the two is the mistake this project keeps rediscovering.
    /// </remarks>
    public static double HeightInDips(int physicalHeight, uint dpi) =>
        dpi == 0 ? physicalHeight : physicalHeight * 96.0 / dpi;

    private static void Fit(Window window, IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpi, out _) != 0) dpi = 96;

        double cap = HeightInDips(info.Work.Bottom - info.Work.Top, dpi);
        if (Math.Abs(window.MaxHeight - cap) < 0.5) return;

        window.MaxHeight = cap;
        Diagnostics.BringUpLog.Write($"settings: height capped to work area {cap:0.#} DIP (dpi {dpi})");
    }

    private static IntPtr MonitorUnderCursor() =>
        GetCursorPos(out POINT cursor) ? MonitorFromPoint(cursor, MonitorDefaultToNearest) : IntPtr.Zero;

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
