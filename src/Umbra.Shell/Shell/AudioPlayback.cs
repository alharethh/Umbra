using System.Runtime.InteropServices;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Whether any application has an active audio stream on any playback device.
/// </summary>
/// <remarks>
/// <para>
/// The bar's stand-in for "a video is playing" (<see cref="Rendering.AwayBlackout"/>). Options
/// weighed and why they lost:
/// </para>
/// <list type="bullet">
/// <item>Display-required power requests are exactly the right signal, but another process's
/// requests are readable only elevated (<c>powercfg /requests</c> refuses otherwise).</item>
/// <item>Windows' media sessions (System Media Transport Controls) report playing and paused
/// precisely, but only through WinRT, which would move the whole project to a versioned Windows
/// target and add the Windows SDK projection to a self-contained download.</item>
/// <item>A fullscreen window already puts the bar behind it, so it needs no answer from here.</item>
/// </list>
/// <para>
/// Core Audio needs neither. Its limits, stated rather than hidden: a muted or silent video does
/// not count, and anything with sound does — music, a call, a game — so the bar stays lit while
/// it plays. An application that holds a stream open while silent keeps the bar lit too; the
/// first such process is logged with its id so it can be found.
/// </para>
/// <para>
/// <b>Fails towards "playing"</b>: if the question cannot be asked, the bar stays lit. A bar that
/// blacks out under a video is the outcome the owner ruled out; one that stays lit is merely the
/// bar as it was.
/// </para>
/// </remarks>
internal static class AudioPlayback
{
    private static bool _loggedFailure;
    private static uint _loggedPid;

    public static bool AnyStreamActive()
    {
        object? enumerator = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid)!);
            Check(((IMMDeviceEnumerator)enumerator!).EnumAudioEndpoints(ERender, DeviceStateActive, out IMMDeviceCollection devices));
            try
            {
                Check(devices.GetCount(out uint count));
                for (uint i = 0; i < count; i++)
                {
                    Check(devices.Item(i, out IMMDevice device));
                    try
                    {
                        if (DeviceHasActiveSession(device)) return true;
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally { Marshal.ReleaseComObject(devices); }

            return false;
        }
        catch (Exception e)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                BringUpLog.Write($"away: audio sessions unreadable, treating as playing ({e.GetType().Name}: {e.Message})");
            }
            return true;
        }
        finally
        {
            if (enumerator is not null && Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
        }
    }

    private static bool DeviceHasActiveSession(IMMDevice device)
    {
        Guid iid = typeof(IAudioSessionManager2).GUID;
        Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object managerObject));
        var manager = (IAudioSessionManager2)managerObject;
        try
        {
            Check(manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions));
            try
            {
                Check(sessions.GetCount(out int count));
                for (int i = 0; i < count; i++)
                {
                    Check(sessions.GetSession(i, out IAudioSessionControl2 session));
                    try
                    {
                        if (session.GetState(out int state) != 0 || state != AudioSessionStateActive) continue;

                        // Logged once per process, so "the bar never goes black" can be traced to
                        // whatever holds a stream open.
                        if (session.GetProcessId(out uint pid) == 0 && pid != _loggedPid)
                        {
                            _loggedPid = pid;
                            BringUpLog.Write($"away: held lit by an active audio session, pid {pid}");
                        }
                        return true;
                    }
                    finally { Marshal.ReleaseComObject(session); }
                }
            }
            finally { Marshal.ReleaseComObject(sessions); }
        }
        finally { Marshal.ReleaseComObject(manager); }

        return false;
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private const int ERender = 0;
    private const int DeviceStateActive = 1;
    private const int ClsctxAll = 0x17;
    private const int AudioSessionStateActive = 1;

    // Method order is the contract: every slot up to the last one used is declared.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notifications);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }
}
