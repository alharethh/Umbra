using Umbra.Shell.Tasks;

namespace Umbra.Shell.Tests;

/// <summary>
/// Which windows the bar is willing to close.
/// </summary>
/// <remarks>
/// Closing File Explorer from the bar took the desktop down with it. The mechanism was never
/// established — a live inventory showed the slot holding an ordinary <c>CabinetWClass</c> window,
/// so the simple "it closed a shell window" theory is disproved — but the rule these tests pin is
/// worth having on its own: for the one process that owns the desktop, guessing wrong ends the
/// session, so it gets a whitelist rather than a blacklist.
/// </remarks>
public class WindowSafetyTests
{
    private static CloseVerdict Ordinary(string className) =>
        WindowSafety.Evaluate(className, isDesktopWindow: false, belongsToShellProcess: false);

    private static CloseVerdict Shell(string className) =>
        WindowSafety.Evaluate(className, isDesktopWindow: false, belongsToShellProcess: true);

    [Fact]
    public void The_Desktop_Is_Never_Closed()
    {
        Assert.False(WindowSafety.Evaluate("CabinetWClass", isDesktopWindow: true,
            belongsToShellProcess: true).Allowed);

        // Even if it somehow presented as an ordinary window of an ordinary process.
        Assert.False(WindowSafety.Evaluate("SomethingElse", isDesktopWindow: true,
            belongsToShellProcess: false).Allowed);
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("SHELLDLL_DefView")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("TrayNotifyWnd")]
    [InlineData("NotifyIconOverflowWindow")]
    public void Shell_Windows_Are_Never_Closed(string className)
    {
        Assert.False(Ordinary(className).Allowed);
        Assert.False(Shell(className).Allowed);
    }

    [Fact]
    public void Shell_Class_Matching_Ignores_Case()
    {
        // Class names come back from Win32 exactly as registered, but nothing guarantees the
        // casing this list was written with.
        Assert.False(Ordinary("progman").Allowed);
        Assert.False(Ordinary("SHELL_TRAYWND").Allowed);
    }

    [Fact]
    public void Ordinary_Application_Windows_Are_Closeable()
    {
        Assert.True(Ordinary("Chrome_WidgetWin_1").Allowed);
        Assert.True(Ordinary("MozillaWindowClass").Allowed);
        Assert.True(Ordinary("SunAwtFrame").Allowed);
        Assert.True(Ordinary("Tauri Window").Allowed);
    }

    /// <summary>
    /// UWP windows must stay closeable. <c>Windows.UI.Core.CoreWindow</c> looks like a shell class
    /// and is not one — it is what every packaged application uses.
    /// </summary>
    [Fact]
    public void Uwp_Windows_Are_Closeable()
    {
        Assert.True(Ordinary("Windows.UI.Core.CoreWindow").Allowed);
        Assert.True(Ordinary("ApplicationFrameWindow").Allowed);
    }

    [Fact]
    public void File_Explorer_Browser_Windows_Are_Closeable_Even_In_The_Shell_Process()
    {
        Assert.True(Shell("CabinetWClass").Allowed);
        Assert.True(Shell("ExploreWClass").Allowed);
    }

    /// <summary>
    /// The whole point: anything else owned by the shell process is refused, because being wrong
    /// about it ends the session rather than merely closing the wrong window.
    /// </summary>
    [Theory]
    [InlineData("Some.Unknown.Shell.Surface")]
    [InlineData("OperationStatusWindow")]
    [InlineData("#32770")]
    [InlineData("")]
    public void Unknown_Shell_Process_Windows_Are_Refused(string className)
    {
        CloseVerdict verdict = Shell(className);

        Assert.False(verdict.Allowed);
        Assert.Contains("shell process", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same classes are ordinary when they are not the shell's, which is what happens when
    /// File Explorer is set to launch folder windows in a separate process.
    /// </summary>
    [Fact]
    public void The_Whitelist_Only_Applies_To_The_Shell_Process()
    {
        Assert.True(Ordinary("OperationStatusWindow").Allowed);
        Assert.True(Ordinary("#32770").Allowed);
    }

    [Fact]
    public void A_Refusal_Always_Says_Why()
    {
        Assert.False(string.IsNullOrWhiteSpace(Shell("Whatever").Reason));
        Assert.False(string.IsNullOrWhiteSpace(Ordinary("Progman").Reason));
        Assert.False(string.IsNullOrWhiteSpace(
            WindowSafety.Evaluate("x", isDesktopWindow: true, belongsToShellProcess: false).Reason));
    }

    [Fact]
    public void A_Null_Handle_Is_Refused_Rather_Than_Throwing()
    {
        CloseVerdict verdict = WindowSafety.Decide(IntPtr.Zero);

        Assert.False(verdict.Allowed);
        Assert.False(WindowSafety.CanClose(IntPtr.Zero));
    }
}
