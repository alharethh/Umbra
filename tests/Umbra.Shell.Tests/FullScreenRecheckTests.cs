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
}
