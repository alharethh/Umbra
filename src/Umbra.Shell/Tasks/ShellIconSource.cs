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

    public ImageSource? Extract(IconCandidate candidate)
    {
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
