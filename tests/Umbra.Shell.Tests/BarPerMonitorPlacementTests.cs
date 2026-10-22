using Umbra.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// A live settings change keeps each bar of a bar-per-monitor set on its own screen.
/// </summary>
/// <remarks>
/// Reported as "changing bar placement duplicates the bar so one is stacked over the other", with
/// two monitors and a bar on each. Every bar was moved to the screen the <c>monitor</c> setting
/// names, so both ended up on it. These run the decision every bar makes, over the set the app
/// builds, with screens as device names because ManagedShell's screen type cannot be made in a test.
/// </remarks>
public class BarPerMonitorPlacementTests
{
    private const string Primary = @"\\.\DISPLAY1";
    private const string Secondary = @"\\.\DISPLAY2";

    private static readonly string[] Attached = [Primary, Secondary];

    /// <summary>What <c>ResolveScreen</c> answers: the named screen, or null for the primary.</summary>
    private static string? Resolve(string monitor) =>
        Attached.FirstOrDefault(name => string.Equals(name, monitor, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where each bar of the set sits after the change, null standing for the primary.</summary>
    private static string[] AfterLiveApply(bool perMonitor, string configuredMonitor) =>
        [.. Attached.Select(current =>
            BarWindow.PlacementScreen(perMonitor, current, () => Resolve(configuredMonitor)) ?? Primary)];

    [Theory]
    [InlineData("")]
    [InlineData(Primary)]
    [InlineData(Secondary)]
    public void Each_Bar_Of_A_Per_Monitor_Set_Keeps_Its_Own_Screen(string configuredMonitor)
    {
        string[] after = AfterLiveApply(perMonitor: true, configuredMonitor);

        Assert.Equal(Attached, after);
        Assert.Equal(after.Length, after.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>A lone bar still goes where the monitor setting says, empty meaning the primary.</summary>
    [Theory]
    [InlineData("", Primary)]
    [InlineData(Secondary, Secondary)]
    [InlineData(@"\\.\DISPLAY9", Primary)]
    public void A_Lone_Bar_Follows_The_Monitor_Setting(string configuredMonitor, string expected)
    {
        string resolved = BarWindow.PlacementScreen(false, Primary, () => Resolve(configuredMonitor)) ?? Primary;

        Assert.Equal(expected, resolved);
    }

    /// <summary>Asking costs a monitor enumeration, so a bar that keeps its screen never asks.</summary>
    [Fact]
    public void A_Bar_That_Keeps_Its_Screen_Does_Not_Look_Up_The_Configured_One()
    {
        bool asked = false;

        BarWindow.PlacementScreen<string>(true, Secondary, () => { asked = true; return Primary; });

        Assert.False(asked);
    }
}
