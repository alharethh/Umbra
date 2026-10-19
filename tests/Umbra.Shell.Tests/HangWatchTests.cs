using System.Diagnostics;
using System.Windows.Interop;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// When the crash guard decides a bar that is still alive has hung.
/// </summary>
/// <remarks>
/// A hung bar keeps Explorer's taskbar suppressed for as long as nobody kills it, and the guard
/// used to wait on its process handle, which a hang never signals. Measured on the second machine
/// with a Debug fault hook (2026-10-19). Both halves are tested: the counting, so one slow answer
/// never ends a bar, and the question itself, against a real window on a thread that stops
/// pumping, because <c>Process.Responding</c> said <c>True</c> about the hung bar for two minutes.
/// </remarks>
public class HangWatchTests
{
    [Fact]
    public void A_Bar_Is_Ended_Only_After_Consecutive_Silences()
    {
        HangWatch watch = new();

        for (int i = 1; i < HangWatch.Strikes; i++)
            Assert.False(watch.Record(answered: false));

        Assert.True(watch.Record(answered: false));
    }

    [Fact]
    public void One_Answer_Starts_The_Count_Again()
    {
        HangWatch watch = new();

        for (int i = 1; i < HangWatch.Strikes; i++) watch.Record(answered: false);
        watch.Record(answered: true);

        for (int i = 1; i < HangWatch.Strikes; i++)
            Assert.False(watch.Record(answered: false));
    }

    [Fact]
    public void The_Strikes_Add_Up_To_Well_Over_Windows_Own_Hung_Threshold()
    {
        // Windows calls a window hung after 5 s without pumping. Ending a bar faster than that
        // would end bars Windows itself still considers merely busy.
        Assert.True(HangWatch.Interval * HangWatch.Strikes >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_Window_Whose_Thread_Stops_Pumping_Does_Not_Answer()
    {
        using ManualResetEventSlim created = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim block = new();
        HwndSource? source = null;

        Thread owner = new(() =>
        {
            // A visible popup far off screen: the probe looks for the first visible top-level
            // window the process owns, which in a test host is this one.
            source = new HwndSource(new HwndSourceParameters("hang-watch-test")
            {
                WindowStyle = unchecked((int)0x90000000), // WS_POPUP | WS_VISIBLE
                PositionX = -32000,
                PositionY = -32000,
                Width = 1,
                Height = 1,
            });

            created.Set();

            // Pump until asked to stop pumping, then stop, as a hung UI thread does.
            while (!block.IsSet)
            {
                PumpOnce();
                Thread.Sleep(10);
            }

            // Not release.Wait(): a managed wait on an STA thread pumps messages, so the window
            // would go on answering. Thread.Sleep does not pump, which is what a hang looks like.
            while (!release.IsSet) Thread.Sleep(10);
            source.Dispose();
        });
        owner.SetApartmentState(ApartmentState.STA);
        owner.IsBackground = true;
        owner.Start();

        Assert.True(created.Wait(TimeSpan.FromSeconds(10)));

        using Process self = Process.GetCurrentProcess();
        Assert.True(HangWatch.Answers(self));

        block.Set();
        Thread.Sleep(100);
        Assert.False(HangWatch.Answers(self));

        release.Set();
        owner.Join(TimeSpan.FromSeconds(10));
    }

    private static void PumpOnce()
    {
        while (PeekMessage(out Msg message, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg message, IntPtr window, uint min, uint max, uint remove);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);
}
