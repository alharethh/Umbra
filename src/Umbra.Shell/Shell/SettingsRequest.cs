using System.Runtime.InteropServices;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Lets a second launch of Umbra ask the running bar to open its settings window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why:</b> the settings live behind a right-click on the bar, which nobody finds without being
/// told. Starting a running application again is what people do instead, and before this it did
/// nothing at all — the duplicate is refused, silently, because only one Umbra may run. Now the
/// refused duplicate sets this event on its way out and the running bar opens its settings.
/// </para>
/// <para>
/// <b>The single-instance rule is untouched.</b> The duplicate signals only <i>after</i> its claim
/// on the lock has failed, and it never constructs a <c>ShellHost</c>, so it still never reaches
/// Explorer. The listener is created only by the process that holds the lock, as
/// <c>DevControl</c> is, so the signal can only reach the bar that is actually running.
/// </para>
/// <para>
/// <b>Not from a restart.</b> A restart passes <c>--wait-for</c> and races its predecessor on
/// purpose; if it loses that race, opening the settings window would be a surprise nobody asked
/// for. Only an ordinary launch signals.
/// </para>
/// </remarks>
internal sealed class SettingsRequest : IDisposable
{
    /// <summary>The event's name. Per session, like the single-instance lock.</summary>
    public const string DefaultEventName = @"Local\Umbra.OpenSettings";

    private readonly EventWaitHandle _event;
    private readonly ManualResetEvent _stop = new(initialState: false);
    private readonly Thread _listener;

    /// <summary>Listens for requests. <paramref name="requested"/> runs on a background thread.</summary>
    /// <remarks>
    /// <para>
    /// <b>A thread of our own, not <c>ThreadPool.RegisterWaitForSingleObject</c>.</b> The pool's
    /// wait thread holds its own reference to a registered event and drops it on its own
    /// schedule, after it has signalled any handle passed to <c>Unregister</c>, so nothing public
    /// says when it has let go. Until it does, disposing ours does not close the event, and a
    /// launch in that moment opens it, sets it and believes a bar is listening. Measured at about
    /// 1 in 2,000 disposals under load: the flake in
    /// <c>SettingsRequestTests.A_Disposed_Listener_Hears_Nothing</c>. Waiting on the unregister
    /// handle measured no better, for that reason.
    /// </para>
    /// <para>
    /// With a thread we own, <see cref="Dispose"/> joins it, and only then closes the event, so
    /// on return nothing else holds it and it is gone. One idle thread in a kernel wait.
    /// </para>
    /// </remarks>
    public SettingsRequest(Action requested, string? name = null)
    {
        _event = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, name ?? DefaultEventName);

        _listener = new Thread(() =>
        {
            WaitHandle[] handles = [_event, _stop];
            while (WaitHandle.WaitAny(handles) == 0)
                requested();
        })
        {
            IsBackground = true,
            Name = "Umbra settings request",
        };
        _listener.Start();
    }

    /// <summary>
    /// Asks the running bar to open its settings. False if no bar is listening.
    /// </summary>
    /// <remarks>
    /// The duplicate is the foreground process — the user has just started it — so it hands its
    /// right to set the foreground on before signalling. Without that the window would open
    /// behind whatever the user was looking at and lean on <c>WindowOrder</c>'s fallbacks.
    /// </remarks>
    public static bool Signal(string? name = null)
    {
        if (!EventWaitHandle.TryOpenExisting(name ?? DefaultEventName, out EventWaitHandle? handle))
            return false;

        using (handle)
        {
            AllowSetForegroundWindow(AsfwAny);
            return handle.Set();
        }
    }

    /// <summary>Stops listening. On return no launch can find the event.</summary>
    /// <remarks>
    /// The join is bounded so a request handler that never returns cannot hold up the bar's exit.
    /// The bar's handler only queues work on the dispatcher, so that should never happen; if it
    /// does, the handles are left open rather than closed under a thread still waiting on them,
    /// and the process is on its way out anyway.
    /// </remarks>
    public void Dispose()
    {
        _stop.Set();
        if (!_listener.Join(StopTimeout))
        {
            BringUpLog.Write("settings request: the listener did not stop; leaving its handles open");
            return;
        }

        _event.Dispose();
        _stop.Dispose();
    }

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>What a refused duplicate does, as one testable decision.</summary>
    internal static bool ShouldSignal(bool restarting) => !restarting;

    /// <summary>The line the duplicate logs, so the bar's log says why a window opened.</summary>
    internal static void LogOutcome(bool signalled) => BringUpLog.Write(signalled
        ? "another Umbra holds the bar; asked it to open its settings and exiting"
        : "another Umbra holds the bar but is not listening for settings requests; exiting");
}
