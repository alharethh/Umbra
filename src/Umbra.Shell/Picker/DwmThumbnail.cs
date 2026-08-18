using System.Runtime.InteropServices;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Picker;

/// <summary>
/// A live thumbnail of another window, composited by DWM into a rectangle of one of our windows.
/// </summary>
/// <remarks>
/// <para>
/// This is the only way to show what a window actually contains without asking it to redraw.
/// The Desktop Window Manager already holds every window's composited surface, and
/// <c>DwmRegisterThumbnail</c> asks it to blit that surface into a rectangle of ours. It updates
/// itself; nothing here has to poll or repaint.
/// </para>
/// <para>
/// <b>Three constraints, each of which silently produces an empty rectangle rather than an
/// error.</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>The destination must not be a layered window.</b> DWM will not composite a thumbnail onto
/// one, and WPF makes a window layered the moment <c>AllowsTransparency="True"</c> is set. The
/// picker therefore has no transparency, which is no loss — it is drawn on pure black anyway (§4).
/// </item>
/// <item>
/// <b>The destination rectangle is in physical pixels</b>, relative to the destination window's
/// client area. WPF lays out in DIP, so every rectangle handed over here has to be scaled by the
/// window's DPI factor first. At 125% an unscaled rectangle produces a thumbnail four fifths of
/// the intended size, tucked into the top-left corner — which reads as a layout bug, not a units
/// bug.
/// </item>
/// <item>
/// <b>DWM composites the thumbnail over the destination window's own content.</b> Anything drawn
/// inside the destination rectangle is hidden, so the close button and title have to live outside
/// it rather than on top of it. That is why the picker puts them in a header strip above each
/// thumbnail rather than overlaying them, which is also what the Windows taskbar does.
/// </item>
/// </list>
/// <para>
/// A minimised window has no live surface, so its thumbnail is blank. Callers should check
/// <see cref="SourceSize"/> and fall back to the application icon rather than showing a hole.
/// </para>
/// </remarks>
internal sealed class DwmThumbnail : IDisposable
{
    private IntPtr _handle;

    private DwmThumbnail(IntPtr handle, Size sourceSize)
    {
        _handle = handle;
        SourceSize = sourceSize;
    }

    /// <summary>The source window's size, or an empty size when DWM has no surface for it.</summary>
    public Size SourceSize { get; }

    /// <summary>Whether DWM has anything to show for this window.</summary>
    public bool HasContent => SourceSize.Width > 0 && SourceSize.Height > 0;

    /// <summary>
    /// Registers a thumbnail of <paramref name="source"/> inside <paramref name="destination"/>,
    /// or null when DWM refuses.
    /// </summary>
    public static DwmThumbnail? Register(IntPtr destination, IntPtr source)
    {
        if (destination == IntPtr.Zero || source == IntPtr.Zero) return null;

        int hr = DwmRegisterThumbnail(destination, source, out IntPtr handle);
        if (hr != 0 || handle == IntPtr.Zero)
        {
            BringUpLog.Write($"DwmRegisterThumbnail failed: 0x{hr:X8}");
            return null;
        }

        Size size = default;
        if (DwmQueryThumbnailSourceSize(handle, out NativeSize native) == 0)
            size = new Size(native.Width, native.Height);

        return new DwmThumbnail(handle, size);
    }

    /// <summary>Current opacity, 0–255. Kept so <see cref="SetOpacity"/> can re-place cheaply.</summary>
    private byte _opacity = 255;
    private NativeRect _destination;

    /// <summary>
    /// Fades the thumbnail, which is the only way to animate one.
    /// </summary>
    /// <remarks>
    /// A WPF <c>RenderTransform</c> does nothing to a thumbnail: DWM composites it into a rectangle
    /// of its own, over our content, so scaling the element underneath scales an invisible box and
    /// leaves the picture where it was. Opacity is a property DWM itself understands, so it is the
    /// press feedback the picker uses.
    /// </remarks>
    public void SetOpacity(byte opacity)
    {
        if (_handle == IntPtr.Zero) return;

        _opacity = opacity;
        Apply(visible: true);
    }

    /// <summary>
    /// Places the thumbnail. <paramref name="destination"/> is in <b>physical pixels</b> relative
    /// to the destination window's client area — see the type remarks.
    /// </summary>
    public void Place(int left, int top, int right, int bottom, bool visible = true)
    {
        if (_handle == IntPtr.Zero) return;

        _destination = new NativeRect { Left = left, Top = top, Right = right, Bottom = bottom };
        Apply(visible);
    }

    private void Apply(bool visible)
    {
        var properties = new ThumbnailProperties
        {
            Flags = FlagDestination | FlagVisible | FlagOpacity | FlagSourceClientAreaOnly,
            Destination = _destination,
            Opacity = _opacity,
            Visible = visible,

            // The client area only, so the source window's title bar and border are not reproduced
            // inside our own frame. It is also the smaller lit area, which P1 prefers.
            SourceClientAreaOnly = true,
        };

        int hr = DwmUpdateThumbnailProperties(_handle, ref properties);
        if (hr != 0) BringUpLog.Write($"DwmUpdateThumbnailProperties failed: 0x{hr:X8}");
    }

    /// <summary>
    /// The largest rectangle with the source window's aspect ratio that fits the given box,
    /// centred. Returns the whole box when the source size is unknown.
    /// </summary>
    public (double X, double Y, double Width, double Height) Fit(double boxWidth, double boxHeight)
    {
        if (!HasContent || boxWidth <= 0 || boxHeight <= 0) return (0, 0, boxWidth, boxHeight);

        double scale = Math.Min(boxWidth / SourceSize.Width, boxHeight / SourceSize.Height);

        // Never upscale: a small source window blown up to fill the cell looks broken, and a
        // 200 px-wide thumbnail of a 120 px window is not more informative than the window.
        scale = Math.Min(scale, 1.0);

        double width = SourceSize.Width * scale;
        double height = SourceSize.Height * scale;
        return ((boxWidth - width) / 2.0, (boxHeight - height) / 2.0, width, height);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;

        DwmUnregisterThumbnail(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>A source size in physical pixels.</summary>
    public readonly record struct Size(int Width, int Height);

    private const int FlagDestination = 0x00000001;
    private const int FlagOpacity = 0x00000004;
    private const int FlagVisible = 0x00000008;
    private const int FlagSourceClientAreaOnly = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public int Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;

        // Win32 BOOL is four bytes. Left to the default marshalling these would be one byte each
        // and every field after them would be read from the wrong offset.
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out NativeSize size);
}
