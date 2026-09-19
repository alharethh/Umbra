using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Umbra.Shell.Shell;

/// <summary>
/// The COM surface for reading a shell link, shared by everything that needs one.
/// </summary>
/// <remarks>
/// Two callers, reading links from two different places: jump list tasks come as raw bytes inside a
/// <c>.customDestinations-ms</c> and load through <see cref="IPersistStream"/>, while Start Menu
/// shortcuts are <c>.lnk</c> files and load through <see cref="IPersistFile"/>. Everything after the
/// load is identical, so the declarations live here rather than being written out twice — a second
/// copy of an interface whose method <i>order</i> is the contract is a good way to acquire a silent
/// memory bug.
/// </remarks>
internal static class ShellLinkInterop
{
    public static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

    /// <summary><c>System.Title</c> — what an application sets to label a jump list task.</summary>
    public static PropertyKey TitleKey = new()
    {
        FormatId = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
        PropertyId = 2,
    };

    /// <summary>Creates an unpopulated shell link, ready to be loaded.</summary>
    public static object? Create()
    {
        Type? type = Type.GetTypeFromCLSID(ClsidShellLink);
        return type is null ? null : Activator.CreateInstance(type);
    }

    /// <summary>Reads the parts of a loaded link that anything here cares about.</summary>
    public static (string Path, string Arguments, string IconPath, int IconIndex) Describe(object link)
    {
        var shell = (IShellLinkW)link;

        StringBuilder path = new(520);
        shell.GetPath(path, path.Capacity, IntPtr.Zero, 0);

        StringBuilder arguments = new(2048);
        shell.GetArguments(arguments, arguments.Capacity);

        StringBuilder icon = new(520);
        shell.GetIconLocation(icon, icon.Capacity, out int iconIndex);

        return (path.ToString(), arguments.ToString(), icon.ToString(), iconIndex);
    }

    /// <summary>
    /// A loaded link's <c>System.Title</c>, whatever variant type it was stored as.
    /// </summary>
    /// <remarks>
    /// Applications write it as <c>VT_LPWSTR</c> or <c>VT_BSTR</c> as they please — reading only
    /// the first spelling gives Firefox its task names and Edge a column of blanks.
    /// <c>PropVariantToStringAlloc</c> asks for the string rather than for a representation.
    /// </remarks>
    public static string Title(object link)
    {
        if (link is not IPropertyStore store) return string.Empty;

        // sizeof(PROPVARIANT) on 64-bit. Allocated rather than stack-based because the shell owns
        // what it writes here and PropVariantClear has to be handed the same address.
        IntPtr variant = Marshal.AllocCoTaskMem(24);

        try
        {
            // Zeroed first. Asking for a key the store does not carry leaves the buffer untouched,
            // and an uninitialised one would be read as whatever type its bytes happened to spell.
            for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);

            store.GetValue(ref TitleKey, variant);
            PropVariantToStringAlloc(variant, out string title);

            return title ?? string.Empty;
        }
        catch (COMException)
        {
            return string.Empty;
        }
        finally
        {
            PropVariantClear(variant);
            Marshal.FreeCoTaskMem(variant);
        }
    }

    [DllImport("shlwapi.dll")]
    public static extern IStream SHCreateMemStream(byte[] init, uint size);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr variant);

    [DllImport("propsys.dll", PreserveSig = false)]
    private static extern void PropVariantToStringAlloc(
        IntPtr variant, [MarshalAs(UnmanagedType.LPWStr)] out string value);

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr find, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, IntPtr value);
        void SetValue(ref PropertyKey key, IntPtr value);
        void Commit();
    }

    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistStream
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load(IStream stream);
        void Save(IStream stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
        void GetSizeMax(out long size);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
