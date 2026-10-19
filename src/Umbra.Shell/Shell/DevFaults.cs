#if DEBUG
using System.Windows.Threading;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Shell;

/// <summary>
/// Lets a test script make the running bar die in each of the ways a real fault would.
/// <b>Debug builds only</b>, like <see cref="DevControl"/> beside it; a release build does not
/// contain this class.
/// </summary>
/// <remarks>
/// <para>
/// Every exit path has to give Explorer's taskbar back, and the paths that matter most are the
/// ones nobody chooses: an exception on the UI thread, one on a raw thread, one in a task nobody
/// awaited, a hang. None of them can be produced from outside the process, so until these hooks
/// existed QA's cases 0.3 and 0.4 were skipped and the handlers in <c>App</c> had only ever run
/// by accident (QA 0.7b's startup crash). Set the named event and the bar faults on the spot.
/// </para>
/// <para>
/// The startup fault is a flag instead, <see cref="StartupFlag"/>, because an event cannot reach
/// a process that has not finished starting. It throws after <c>ShellHost</c> has suppressed
/// Explorer and the crash guard is launched, which is the worst moment to fail: the taskbar is
/// already gone and no bar has been drawn yet.
/// </para>
/// </remarks>
internal sealed class DevFaults : IDisposable
{
    /// <summary>Throws on the UI thread, from a dispatcher callback.</summary>
    public const string UiEventName = @"Local\Umbra.Dev.Fault.Ui";

    /// <summary>Throws on a new, raw <see cref="Thread"/>.</summary>
    public const string ThreadEventName = @"Local\Umbra.Dev.Fault.Thread";

    /// <summary>
    /// Throws inside a <see cref="Task"/> nobody observes, then forces the finalizer that reports it.
    /// </summary>
    public const string TaskEventName = @"Local\Umbra.Dev.Fault.Task";

    /// <summary>Blocks the UI thread forever.</summary>
    public const string HangEventName = @"Local\Umbra.Dev.Fault.Hang";

    /// <summary>Command-line flag: throw during startup, after Explorer has been suppressed.</summary>
    public const string StartupFlag = "--dev-fault-startup";

    private readonly List<(EventWaitHandle Event, RegisteredWaitHandle Wait)> _hooks = [];

    public DevFaults(Dispatcher dispatcher)
    {
        Hook(UiEventName, () => dispatcher.BeginInvoke(() =>
            throw new InvalidOperationException("dev fault: UI thread")));

        Hook(ThreadEventName, () => new Thread(() =>
            throw new InvalidOperationException("dev fault: raw thread"))
        { IsBackground = true, Name = "dev fault" }.Start());

        Hook(TaskEventName, () =>
        {
            ThrowInUnobservedTask();

            // Long enough for the task to have run and faulted before it is collected.
            Thread.Sleep(500);

            // An unobserved task is reported only when it is finalised, which otherwise happens
            // whenever the GC next gets round to it. Forced, so a script can read the outcome.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        });

        Hook(HangEventName, () => dispatcher.BeginInvoke(() =>
        {
            BringUpLog.Write("dev fault: hanging the UI thread");
            Thread.Sleep(Timeout.Infinite);
        }));
    }

    /// <summary>True when the command line asks for a startup fault.</summary>
    public static bool StartupRequested(string[] args) =>
        args.Contains(StartupFlag, StringComparer.OrdinalIgnoreCase);

    /// <summary>The startup fault itself, thrown by <c>App.OnStartup</c>.</summary>
    public static void ThrowStartupFault() =>
        throw new InvalidOperationException("dev fault: startup, after Explorer was suppressed");

    // Not inlined into the hook, so no local on that stack frame keeps the task reachable.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowInUnobservedTask()
    {
        _ = Task.Run(() => throw new InvalidOperationException("dev fault: unobserved task"));
    }

    private void Hook(string name, Action fault)
    {
        EventWaitHandle handle = new(initialState: false, EventResetMode.AutoReset, name);

        RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(
            handle,
            (_, _) =>
            {
                BringUpLog.Write($"dev fault requested: {name}");
                fault();
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        _hooks.Add((handle, wait));
    }

    public void Dispose()
    {
        foreach ((EventWaitHandle handle, RegisteredWaitHandle wait) in _hooks)
        {
            wait.Unregister(null);
            handle.Dispose();
        }

        _hooks.Clear();
    }
}
#endif
