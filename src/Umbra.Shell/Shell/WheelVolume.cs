using System.Runtime.InteropServices;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>The master volume of the default playback device, as far as the wheel needs it.</summary>
internal interface IMasterVolume
{
    /// <summary>The level, 0 to 1, as the endpoint reports it; NaN when it cannot be read.</summary>
    float Level { get; set; }

    bool Muted { get; set; }
}

/// <summary>
/// Rolling the wheel over the system cluster changes the volume, as it does over the speaker on
/// Windows 11's own taskbar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two percent a notch</b>, the step Windows 11 uses for the wheel over its speaker icon and for
/// the volume keys. The level is read back as a whole percentage before the step is added, so a
/// device sitting at 33.4% goes to 35% and then 37%, and repeated notches never accumulate float
/// error into a level that reads 99% at the top.
/// </para>
/// <para>
/// <b>The deltas are accumulated, not counted.</b> A precision touchpad's two-finger scroll sends a
/// stream of small deltas, a few units each; acting on every event would race the volume to an end
/// in one swipe. A step happens per <c>WHEEL_DELTA</c> (120) collected, the remainder carried, so a
/// mouse notch is exactly one step and a swipe is as many as it adds up to.
/// </para>
/// <para>
/// <b>Up unmutes; down does not.</b> Turning the volume up is a request to hear something, and a
/// level that rises while muted would leave the user scrolling at silence. Turning it down while
/// muted lowers the level the unmute will come back to and leaves the mute alone.
/// </para>
/// <para>
/// <b>Core Audio, not a synthesised volume key.</b> <c>VK_VOLUME_UP</c> would be shorter, but it is
/// delivered to whatever has the keyboard, it raises Windows' own volume pop-up wherever Windows
/// decides to put it with its taskbar suppressed, and it goes through any key remapper the user
/// runs. The endpoint is the level itself.
/// </para>
/// </remarks>
internal sealed class WheelVolume(IMasterVolume volume)
{
    public const int WheelDelta = 120;

    public const int StepPercent = 2;

    private int _carried;

    /// <summary>Applies one wheel event; returns the level and mute state to show, or null if nothing moved.</summary>
    public (int Percent, bool Muted)? Apply(int delta)
    {
        _carried += delta;
        int notches = _carried / WheelDelta;
        _carried -= notches * WheelDelta;

        if (notches == 0) return null;

        // An unreadable level is not 0%: stepping from it would drop the volume to 2%.
        float level = volume.Level;
        if (float.IsNaN(level)) return null;

        int before = Percent(level);
        int after = Math.Clamp(before + notches * StepPercent, 0, 100);

        if (after != before) volume.Level = after / 100f;
        if (notches > 0 && volume.Muted) volume.Muted = false;

        return (after, volume.Muted);
    }

    /// <summary>The current level, for the tooltip shown before any step.</summary>
    public (int Percent, bool Muted) Current => (Percent(volume.Level), volume.Muted);

    /// <summary>Forgets a part-collected notch, so a swipe that ended elsewhere does not finish here.</summary>
    public void Reset() => _carried = 0;

    public static int Percent(float level) => (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100f);

    /// <summary>What the tooltip says.</summary>
    public static string Describe(int percent, bool muted) =>
        muted ? $"Volume: {percent}% (muted)" : $"Volume: {percent}%";
}

/// <summary>
/// <see cref="IMasterVolume"/> on the default render endpoint, through <c>IAudioEndpointVolume</c>.
/// </summary>
/// <remarks>
/// Resolved on every use rather than held: the default device changes when headphones are plugged
/// in, and a held endpoint would go on turning the speakers up. A wheel notch is rare enough that
/// the lookup costs nothing that matters.
/// </remarks>
internal sealed class DefaultEndpointVolume : IMasterVolume
{
    public float Level
    {
        get => With(v => { Check(v.GetMasterVolumeLevelScalar(out float l)); return l; }, float.NaN);
        set => With(v => { Guid ctx = Guid.Empty; Check(v.SetMasterVolumeLevelScalar(value, ref ctx)); return 0; }, 0);
    }

    public bool Muted
    {
        get => With(v => { Check(v.GetMute(out bool m)); return m; }, false);
        set => With(v => { Guid ctx = Guid.Empty; Check(v.SetMute(value, ref ctx)); return 0; }, 0);
    }

    /// <summary>Whether there is a default playback device at all.</summary>
    public static bool Available => With(_ => true, false);

    private static T With<T>(Func<IAudioEndpointVolume, T> use, T fallback)
    {
        object? enumerator = null, device = null, endpoint = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid)!);
            Check(((IMMDeviceEnumerator)enumerator!).GetDefaultAudioEndpoint(ERender, EMultimedia, out var d));
            device = d;
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            Check(d.Activate(ref iid, ClsctxAll, IntPtr.Zero, out endpoint));
            return use((IAudioEndpointVolume)endpoint);
        }
        catch (Exception e)
        {
            // No playback device is an ordinary state (a desktop with nothing plugged in), and
            // the wheel then does nothing; logged so it is never a mystery.
            BringUpLog.Write($"wheel volume: endpoint unavailable ({e.GetType().Name}: {e.Message})");
            return fallback;
        }
        finally
        {
            foreach (var o in new[] { endpoint, device, enumerator })
                if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private const int ERender = 0;
    private const int EMultimedia = 1;
    private const int ClsctxAll = 0x17;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    // Method order is the contract: every slot up to the last one used is declared.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
