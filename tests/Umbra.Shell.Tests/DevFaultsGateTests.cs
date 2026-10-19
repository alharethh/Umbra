namespace Umbra.Shell.Tests;

/// <summary>
/// The fault hooks that make the bar crash or hang on request are in Debug builds and nowhere
/// else. Read off the built assembly, as <see cref="MotionSpeedGateTests"/> does, and asserted
/// both ways so <c>dotnet test -c Release</c> proves the absence.
/// </summary>
public class DevFaultsGateTests
{
#if DEBUG
    private const bool Debug = true;
#else
    private const bool Debug = false;
#endif

    [Fact]
    public void The_Fault_Hooks_Exist_Only_In_Debug()
    {
        Assert.Equal(Debug, typeof(BarWindow).Assembly.GetType("Umbra.Shell.Shell.DevFaults") is not null);
    }
}
