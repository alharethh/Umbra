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

    /// <summary>
    /// Explorer's <c>TaskbarAl</c> setting — 0 for left, 1 for centre — or <see langword="null"/>
    /// when the value is absent and Windows is using its own default.
    /// </summary>
    /// <remarks>
    /// The Start menu positions itself from this and from where Explorer believes the taskbar to
    /// be, so §7.0 writes it to match Umbra's own alignment and restores it on exit. Absent is a
    /// distinct state from zero: putting a value back that was never there is still a change to the
    /// user's machine.
    /// </remarks>
    int? GetAlignment();

    /// <summary>Sets, or with <see langword="null"/> removes, <c>TaskbarAl</c>.</summary>
    void SetAlignment(int? alignment);
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

    /// <summary>Where Explorer keeps the taskbar alignment (§7.0).</summary>
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const string AlignmentValue = "TaskbarAl";

    public int? GetAlignment()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return key?.GetValue(AlignmentValue) as int?;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void SetAlignment(int? alignment)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvancedKey, writable: true);

            if (key is null) return;

            if (alignment is null) key.DeleteValue(AlignmentValue, throwOnMissingValue: false);
            else key.SetValue(AlignmentValue, alignment.Value, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Not being able to move the Start menu is a cosmetic loss, not a reason to fail.
        }
    }
}
