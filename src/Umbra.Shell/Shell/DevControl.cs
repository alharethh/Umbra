#if DEBUG
using System.Windows.Threading;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Lets a development script ask the running bar to exit cleanly. <b>Debug builds only</b> — a
/// release build does not contain this class.
/// </summary>
/// <remarks>
/// <para>
/// Nothing outside the process can close the bar: <c>AppBarWindow</c> cancels <c>WM_CLOSE</c>
/// unless <c>AllowClose</c> is set, which only the bar's own Exit does, and a hard kill skips the
/// clean shutdown that hands Explorer's taskbar back. So every rebuild during development needed
/// someone to right-click → Exit by hand. <c>tools/dev-restart.ps1</c> sets this event instead,
/// and the bar takes exactly the path its Exit menu item takes.
/// </para>
/// <para>
/// Created only by the instance that holds the single-instance lock, so a refused duplicate never
/// listens and a signal can only ever reach the bar that is actually running.
/// </para>
/// </remarks>
internal sealed class DevControl : IDisposable
{
    public const string ExitEventName = @"Local\Umbra.Dev.Exit";

    private readonly EventWaitHandle _exit;
    private readonly RegisteredWaitHandle _wait;

    public DevControl(Dispatcher dispatcher, Action exit)
    {
        _exit = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ExitEventName);

        _wait = ThreadPool.RegisterWaitForSingleObject(
            _exit,
            (_, _) => dispatcher.BeginInvoke(() =>
            {
                BringUpLog.Write("dev: exit requested by tools/dev-restart.ps1");
                exit();
            }),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _exit.Dispose();
    }
}
#endif
