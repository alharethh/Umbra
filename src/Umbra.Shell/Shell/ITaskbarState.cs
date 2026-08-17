using ManagedShell.AppBar;
using ManagedShell.Interop;

namespace Umbra.Shell.Shell;

/// <summary>
/// Explorer's taskbar state, behind an interface so <see cref="ExplorerStateGuard"/> can be tested
/// without a live shell.
/// </summary>
/// <remarks>
/// The abstraction earns its place: whether the guard restores correctly depends on the machine's
/// starting configuration, and on a machine whose taskbar already autohides the restore is a no-op
/// and proves nothing. The interesting case — autohide off, Umbra turns it on, Umbra must turn it
/// back off — cannot be exercised without changing the user's settings.
/// </remarks>
internal interface ITaskbarState
{
    /// <summary>The current AppBar state of Explorer's taskbar.</summary>
    NativeMethods.ABState GetState();

    /// <summary>Sets the AppBar state of Explorer's taskbar.</summary>
    void SetState(NativeMethods.ABState state);

    /// <summary>Whether Explorer's taskbar is being actively suppressed and re-hidden.</summary>
    bool Hidden { get; set; }
}

/// <summary>The live implementation, over ManagedShell's <see cref="ExplorerHelper"/>.</summary>
internal sealed class ExplorerTaskbarState : ITaskbarState
{
    private readonly ExplorerHelper _explorerHelper;

    public ExplorerTaskbarState(ExplorerHelper explorerHelper)
    {
        ArgumentNullException.ThrowIfNull(explorerHelper);
        _explorerHelper = explorerHelper;
    }

    public NativeMethods.ABState GetState() => _explorerHelper.GetTaskbarState();

    public void SetState(NativeMethods.ABState state) => _explorerHelper.SetTaskbarState(state);

    public bool Hidden
    {
        get => _explorerHelper.HideExplorerTaskbar;
        set => _explorerHelper.HideExplorerTaskbar = value;
    }
}
