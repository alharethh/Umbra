using System.Diagnostics;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// The startup fullscreen check calls a private ManagedShell method by name. A ManagedShell update
/// that renames or reshapes it would silently bring back a bar drawn over a game it started under.
/// </summary>
public class FullScreenRecheckTests
{
    [Fact]
    public void ManagedShell_Still_Has_The_Check_It_Calls()
    {
        Assert.NotNull(FullScreenRecheck.Method);
        Assert.Empty(FullScreenRecheck.Method!.GetParameters());
    }

    // SameAppHold asks this about the window the bar gave way to. Missing, it answers false and
    // clicking a fullscreen player's own controls brings the bar up again.
    [Fact]
    public void ManagedShell_Still_Has_The_Per_Window_Check()
    {
        Assert.NotNull(FullScreenRecheck.WindowMethod);
        Assert.Equal(typeof(ManagedShell.AppBar.FullScreenApp), FullScreenRecheck.WindowMethod!.ReturnType);
    }

    // Live, 2026-11-17: VLC went back to a window with its controls in front, and the bar stayed
    // held down until VLC closed. Asked in the form the app was listed with, ManagedShell skips the
    // covers-the-screen test for anything Windows reported fullscreen, so a plain window answered
    // "still fullscreen". The window has to be another process's: ManagedShell never calls one of
    // ours fullscreen, which would make this pass however it is asked.
    [Fact]
    public async Task A_Window_That_Does_Not_Cover_Its_Screen_Is_Not_Still_Fullscreen()
    {
        using Process child = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -NonInteractive -Command \"Add-Type -AssemblyName System.Windows.Forms; " +
                        "$f = New-Object System.Windows.Forms.Form; $f.ShowInTaskbar = $false; " +
                        "$f.StartPosition = 'Manual'; $f.Location = New-Object System.Drawing.Point(-32000, -32000); " +
                        "$f.Show(); [Console]::Out.WriteLine($f.Handle.ToInt64()); [Console]::Out.Flush(); " +
                        "while ($true) { [System.Windows.Forms.Application]::DoEvents(); [System.Threading.Thread]::Sleep(50) }\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

        try
        {
            // Not MainWindowHandle: a form kept off the taskbar is owned by a hidden window, and
            // Process does not count an owned window as the main one.
            string? line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            IntPtr window = new(long.Parse(line!));
            using ManagedShell.WindowsTasks.TasksService tasks = new();
            using ManagedShell.AppBar.FullScreenHelper helper = new(tasks);

            ManagedShell.AppBar.FullScreenApp listed = new()
            {
                hWnd = window,
                screen = ManagedShell.AppBar.ScreenInfo.Create(System.Windows.Forms.Screen.FromHandle(window)),
                title = "test",
                fromTasksService = true,
            };

            Assert.False(FullScreenRecheck.StillFullScreen(helper, listed));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }
}
