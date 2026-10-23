using System.Runtime.InteropServices;
using System.Text;

namespace Umbra.Shell.Tasks;

/// <summary>
/// The app user model ID a packaged process runs under, read from the process rather than from
/// its window.
/// </summary>
/// <remarks>
/// <para>
/// <b>A packaged desktop application need not put its ID on its windows.</b> Windows Terminal is
/// the measured case: its window carries no <c>System.AppUserModel.ID</c>, so ManagedShell's
/// <c>AppUserModelID</c> is empty and the slot is keyed by
/// <c>...\WindowsApps\Microsoft.WindowsTerminal_1.24.12741.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe</c>.
/// The process itself has package identity, and <c>GetApplicationUserModelId</c> answers
/// <c>Microsoft.WindowsTerminal_8wekyb3d8bbwe!App</c> — which is what Windows' own taskbar falls
/// back to, and what <c>ActivateApplication</c> needs to start another copy. The path cannot be
/// activated, and a packaged application has no Start Menu shortcut to fall back on.
/// </para>
/// <para>
/// A local kernel query on a handle with the least access there is, so it is cheap and cannot hang
/// on the other process the way a window message can. Unpackaged processes answer
/// <c>APPMODEL_ERROR_NO_APPLICATION</c>, which is null here.
/// </para>
/// </remarks>
internal static class ProcessAppId
{
    /// <summary>The app user model ID of the process that owns <paramref name="window"/>, or null.</summary>
    public static string? For(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;

        GetWindowThreadProcessId(window, out uint pid);
        return pid == 0 ? null : ForProcess(pid);
    }

    /// <summary>The app user model ID process <paramref name="pid"/> runs under, or null.</summary>
    public static string? ForProcess(uint pid)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;

        try
        {
            uint length = 256;
            StringBuilder id = new((int)length);

            int error = GetApplicationUserModelId(process, ref length, id);
            if (error == ErrorInsufficientBuffer)
            {
                id = new StringBuilder((int)length);
                error = GetApplicationUserModelId(process, ref length, id);
            }

            return error == 0 && id.Length > 0 ? id.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);
}
