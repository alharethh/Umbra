using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace Umbra.Shell.Tasks;

/// <summary>
/// The names of packaged applications, resolved from their app user model ID off the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>A packaged application's window does not belong to the application.</b> Settings,
/// Calculator and every other UWP application draw into a <c>Windows.UI.Core.CoreWindow</c> that
/// is a child of an <c>ApplicationFrameWindow</c>, and the frame is owned by
/// <c>ApplicationFrameHost.exe</c>. Measured with ManagedShell's own <c>ApplicationWindow</c> on
/// both: <c>WinFileName</c> is the frame host for both, <c>WinFileDescription</c> is
/// "Application Frame Host" for both, and only <c>AppUserModelID</c> tells them apart. Anything
/// that names or draws a slot from the executable therefore names every such slot after the host
/// and draws the host's generic frame icon.
/// </para>
/// <para>
/// <b>Windows names them through the shell's Apps folder</b>, <c>shell:AppsFolder\&lt;AUMID&gt;</c>,
/// which is what Start and the taskbar show. Resolving that item reads the package manifest, so
/// it runs on a worker exactly like icon extraction does and the getter only ever reads the
/// dictionary. ManagedShell's <c>StoreAppHelper.AppList</c> would also answer, but it is an
/// unsynchronised <c>List</c> that ManagedShell already appends to from its own icon tasks.
/// </para>
/// </remarks>
internal sealed class PackagedAppNames
{
    /// <summary>The instance the bar uses.</summary>
    public static PackagedAppNames Shared { get; } = new();

    private readonly Func<string, string?> _resolve;
    private readonly ConcurrentDictionary<string, string> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Action>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _finished = new(StringComparer.OrdinalIgnoreCase);

    public PackagedAppNames(Func<string, string?>? resolve = null) => _resolve = resolve ?? FromAppsFolder;

    /// <summary>Whether an executable is the frame host that UWP applications are drawn inside.</summary>
    public static bool IsFrameHost(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            return string.Equals(Path.GetFileName(path), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The application's name if it has been resolved; never blocks.</summary>
    public string? Lookup(string? aumid) =>
        !string.IsNullOrWhiteSpace(aumid) && _resolved.TryGetValue(aumid, out string? name) ? name : null;

    /// <summary>
    /// Starts resolving <paramref name="aumid"/> on a worker, once per ID for the life of the
    /// process, and calls <paramref name="onResolved"/> from that worker if a name was found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once, not retried: a packaged application's name is in its manifest and does not arrive
    /// late the way an icon from a cold shell cache can. A worker stranded by a shell call that
    /// never returns costs one thread per ID, and nothing on the UI thread waits for it.
    /// </para>
    /// <para>
    /// <b>Every caller that asks while the worker runs is told, not only the first.</b> The
    /// instance is shared by every bar, so with a bar on every monitor the same ID is asked for by
    /// one slot per bar, and by each bar's model for a pin. Keeping the first caller's callback
    /// alone left the other bars' slots named after the window title or the frame host until
    /// something else happened to raise their name. A caller that asks after the name arrived is
    /// not called back: it reads <see cref="Lookup"/>, as every caller does right after asking.
    /// </para>
    /// </remarks>
    public void Request(string? aumid, Action onResolved)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return;
        if (_resolved.ContainsKey(aumid)) return;

        lock (_gate)
        {
            if (_finished.Contains(aumid)) return;

            if (_pending.TryGetValue(aumid, out List<Action>? waiting))
            {
                waiting.Add(onResolved);
                return;
            }

            _pending[aumid] = [onResolved];
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            string? name;

            try
            {
                name = _resolve(aumid);
            }
            catch (Exception e)
            {
                Diagnostics.BringUpLog.Write($"packaged app name failed: {aumid}: {e.Message}");
                name = null;
            }

            List<Action> callers;

            lock (_gate)
            {
                // Published under the lock, so a caller either joins the list before it is taken
                // or finds the ID finished and reads the name through Lookup.
                if (!string.IsNullOrWhiteSpace(name)) _resolved[aumid] = name;
                _finished.Add(aumid);
                _pending.Remove(aumid, out callers!);
            }

            if (string.IsNullOrWhiteSpace(name)) return;

            foreach (Action callback in callers) callback();
        });
    }

    private static string? FromAppsFolder(string aumid)
    {
        Guid shellItem = typeof(IShellItem).GUID;
        int hr = SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref shellItem, out IShellItem item);
        if (hr != 0) return null;

        try
        {
            item.GetDisplayName(SigdnNormalDisplay, out IntPtr name);

            try
            {
                return Marshal.PtrToStringUni(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private const uint SigdnNormalDisplay = 0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, ref Guid riid, out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
