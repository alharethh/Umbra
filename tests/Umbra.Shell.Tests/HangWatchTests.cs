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
    public void Checks_At_Their_Slowest_Normal_Spacing_Still_Add_Up()
    {
        // An interval plus a timed-out question is the widest a normal gap gets. Mistaking that
        // for a sleep would mean a real hang is never counted.
        long now = 0;
        HangWatch watch = new(() => now);
        long step = (long)(HangWatch.Interval + HangWatch.Timeout).TotalMilliseconds;

        for (int i = 1; i < HangWatch.Strikes; i++)
        {
            now += step;
            Assert.False(watch.Record(answered: false));
        }

        now += step;
        Assert.True(watch.Record(answered: false));
    }

    [Fact]
    public void Silences_After_The_Guard_Itself_Was_Asleep_Are_Not_Counted()
    {
        // Resume from sleep: the guard was frozen as well, its next check is minutes late, and
        // the bar may take a while to start pumping again. Measured as a TickCount64 gap.
        long now = 0;
        HangWatch watch = new(() => now);
        long step = (long)HangWatch.Interval.TotalMilliseconds;

        for (int i = 1; i < HangWatch.Strikes; i++)
        {
            now += step;
            watch.Record(answered: false);
        }

        now += (long)TimeSpan.FromMinutes(20).TotalMilliseconds;
        Assert.False(watch.Record(answered: false));

        long graceEnds = now + (long)HangWatch.Grace.TotalMilliseconds;
        while (now + step < graceEnds)
        {
            now += step;
            Assert.False(watch.Record(answered: false));
        }

        // Grace over: a bar that is still silent is hung, and is counted from scratch.
        now = graceEnds;
        for (int i = 1; i < HangWatch.Strikes; i++)
        {
            now += step;
            Assert.False(watch.Record(answered: false));
        }

        now += step;
        Assert.True(watch.Record(answered: false));
    }

    [Fact]
    public async Task The_Guard_Ends_A_Process_That_Stops_Answering()
    {
        // The wiring, not the counting: CrashGuard's wait has to be the hang watch. Reverting it to
        // a plain WaitForExit left every other test green, and leaves this one waiting on a child
        // that sleeps for ten minutes. The child shows a form and then sleeps without pumping,
        // which is what the hung bar did. Thread.Sleep, not Start-Sleep: PowerShell runs its
        // pipeline on an STA thread, where Start-Sleep's managed wait pumps, and the form answers.
        using Process child = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -NonInteractive -Command \"Add-Type -AssemblyName System.Windows.Forms; " +
                        "$f = New-Object System.Windows.Forms.Form; $f.ShowInTaskbar = $false; " +
                        "$f.StartPosition = 'Manual'; $f.Location = New-Object System.Drawing.Point(-32000, -32000); " +
                        "$f.Show(); [System.Windows.Forms.Application]::DoEvents(); [System.Threading.Thread]::Sleep(600000)\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            Task<bool> watched = Task.Run(() => CrashGuard.Watch(child, TimeSpan.FromMilliseconds(500)));

            Task first = await Task.WhenAny(watched, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(first == watched, "the guard went on waiting on a hung process");
            Assert.True(await watched);
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
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
