using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Umbra.Shell.Tasks;

/// <summary>
/// Reads an icon out of a file, at the size the bar actually draws.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not ManagedShell's <c>IconHelper</c>.</b> That one takes a global
/// <c>ComLock</c> around every extraction, so one slow file serialises every other icon in the
/// process, and it offers only the shell's fixed sizes. Ours is a plain P/Invoke with no lock of
/// its own — the resolver's timeout is the only protection needed, and it is the only one that
/// works against a call that never returns.
/// </para>
/// <para>
/// <b><see cref="PrivateExtractIconsW"/> first, because it takes the size as an argument.</b>
/// <c>SHGetFileInfo</c> hands back whatever the system icon size is — 32 px — and the bar draws
/// icons up to 26 DIP, which is 33 physical pixels at 125% scaling. Upscaling a 32 px icon to 33
/// is a visible softening on artwork that is mostly thin strokes, and the normalisation in
/// <see cref="Rendering.IconPipeline"/> then measures a blurred image. Asking for 48 costs
/// nothing and the downscale is clean.
/// </para>
/// </remarks>
internal sealed class ShellIconSource : IIconSource
{
    /// <summary>
    /// The extent asked of the shell, in pixels. Comfortably above the largest the bar draws.
    /// </summary>
    public const int Extent = 48;

    /// <summary>
    /// The prefix of a candidate that names a packaged application by app ID rather than a file.
    /// </summary>
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>A candidate for a packaged application's own logo. See <see cref="PackagedExecutable"/>.</summary>
    public static IconCandidate ForAppId(string appId) => new(AppsFolderPrefix + appId);

    public ImageSource? Extract(IconCandidate candidate)
    {
        if (candidate.Path?.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase) == true)
            return FromAppsFolder(candidate.Path);

        string path = Expand(candidate.Path);
        if (path.Length == 0) return null;

        return FromPrivateExtract(path, candidate.Index) ?? FromShellFileInfo(path);
    }

    /// <summary>
    /// Splits the <c>"file,index"</c> spelling that shortcuts and relaunch properties both use.
    /// </summary>
    /// <remarks>
    /// The index is frequently negative — that is a resource ID rather than an ordinal, and both
    /// <c>PrivateExtractIcons</c> and the shell understand it as one, so it is passed through
    /// rather than normalised. A bare path with no comma is index 0.
    /// </remarks>
    public static IconCandidate Parse(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource)) return default;

        string value = resource.Trim();
        int comma = value.LastIndexOf(',');

        if (comma <= 0 || !int.TryParse(value[(comma + 1)..].Trim(), out int index))
            return new IconCandidate(value);

        return new IconCandidate(value[..comma].Trim(), index);
    }

    private static string Expand(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        try
        {
            // Relaunch icon resources are documented as carrying environment variables —
            // "%systemdir%\system32\shell32.dll,-128" is Microsoft's own example.
            string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

            return File.Exists(expanded) ? expanded : string.Empty;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static ImageSource? FromPrivateExtract(string path, int index)
    {
        IntPtr[] handles = [IntPtr.Zero];

        int found = PrivateExtractIconsW(path, index, Extent, Extent, handles, null, 1, 0);
        if (found <= 0 || handles[0] == IntPtr.Zero) return null;

        try
        {
            return ToImage(handles[0]);
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }

    /// <summary>
    /// A packaged application's logo, as the Start menu and Windows' taskbar draw it: the shell's
    /// image for its Apps folder item.
    /// </summary>
    /// <remarks>
    /// <c>SIIGBF_ICONONLY</c>, so it is the icon and never a thumbnail. The bitmap comes back with
    /// premultiplied alpha, which <c>CreateBitmapSourceFromHBitmap</c> would flatten onto black,
    /// so its pixels are copied out as they are.
    /// </remarks>
    private static ImageSource? FromAppsFolder(string parsingName)
    {
        Guid factoryId = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref factoryId, out IShellItemImageFactory? factory) != 0
            || factory is null)
            return null;

        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new NativeSize { Width = Extent, Height = Extent }, SiigbfIconOnly, out bitmap) != 0
                || bitmap == IntPtr.Zero)
                return null;

            return FromPremultipliedBitmap(bitmap);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            Marshal.ReleaseComObject(factory);
        }
    }

    private static ImageSource? FromPremultipliedBitmap(IntPtr bitmap)
    {
        if (GetObjectW(bitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap info) == 0) return null;
        if (info.Width <= 0 || info.Height <= 0 || info.BitsPixel != 32) return null;

        int width = info.Width;
        int height = info.Height;
        byte[] pixels = new byte[width * height * 4];

        BitmapInfoHeader header = new()
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
        };

        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }

        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private const uint SiigbfIconOnly = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObjectW(IntPtr handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    private static ImageSource? FromShellFileInfo(string path)
    {
        ShellFileInfo info = default;

        IntPtr result = SHGetFileInfoW(
            path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiLargeIcon);

        if (result == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;

        try
        {
            return ToImage(info.Icon);
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }

    private static ImageSource? ToImage(IntPtr icon)
    {
        try
        {
            ImageSource image = Imaging.CreateBitmapSourceFromHIcon(
                icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Made on a worker, used on the UI thread. The resolver freezes too; doing it here as
            // well means a source used directly is not a trap.
            if (image.CanFreeze) image.Freeze();

            return image;
        }
        catch (COMException)
        {
            // A handle the imaging layer will not accept. Rare, and indistinguishable from "this
            // file has no icon" as far as the caller is concerned.
            return null;
        }
    }

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIconsW(
        string file, int index, int width, int height, IntPtr[] icons, int[]? ids, int count, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfoW(
        string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
