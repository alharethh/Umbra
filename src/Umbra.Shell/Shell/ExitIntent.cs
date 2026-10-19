using System.Globalization;
using System.IO;
using System.Threading;

namespace Umbra.Shell.Shell;

/// <summary>
/// The bar's word to its <see cref="CrashGuard"/> that it is ending on purpose: a named,
/// manual-reset event per bar process, set before anything on the way out that could stall.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The guard relaunches a bar that <see cref="HangWatch"/> ended, because
/// the hang may have been a false positive. A bar that stalls on its way out — in
/// <c>ShellManager.Dispose</c>, in <c>OnExit</c>, or during logoff — also stops answering, and
/// ending it is right, but starting it again would undo the user's Exit, fight a Restart's
/// replacement for the lock, or hand <c>dev-restart.ps1</c> a bar it did not ask for, holding the
/// exe it is about to overwrite.
/// </para>
/// <para>
/// <b>An event rather than a file.</b> It needs no cleanup and cannot go stale: it lives exactly
/// as long as someone holds a handle to it, and its name carries the bar's process id, so it can
/// never be mistaken for a previous bar's. Both sides create-or-open it, so the order does not
/// matter — the guard opens it when it starts, and a bar that declares before the guard exists
/// keeps its own handle until the process ends, which is the whole of the time the guard could
/// look.
/// </para>
/// </remarks>
internal static class ExitIntent
{
    // Held for the life of the process: closing it before the guard opened the event would
    // destroy the object and the declaration with it.
    private static readonly List<EventWaitHandle> Declared = [];

    /// <summary>The event's name for a given bar process.</summary>
    public static string Name(int barId) =>
        string.Create(CultureInfo.InvariantCulture, $@"Local\Umbra.ExitIntent.{barId}");

    /// <summary>Declares that this process is ending on purpose. Safe to call repeatedly.</summary>
    public static void Declare(string why) => Declare(Environment.ProcessId, why);

    /// <summary>Declares for a given process id; the overload exists for tests.</summary>
    internal static void Declare(int barId, string why)
    {
        try
        {
            EventWaitHandle handle = new(false, EventResetMode.ManualReset, Name(barId));
            handle.Set();
            lock (Declared) Declared.Add(handle);
            Diagnostics.BringUpLog.Write($"exit intent declared: {why}");
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            // Without it a stall on the way out that ends before the restore could be relaunched.
            // Logged, because nothing else would show it.
            Diagnostics.BringUpLog.Write(e, "exit intent could not be declared");
        }
    }

    /// <summary>
    /// Opens the bar's event from the guard's side, before the wait. Null when it cannot be
    /// opened, which the guard reads as "no intent", the pre-existing behaviour.
    /// </summary>
    public static EventWaitHandle? Watch(int barId)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.ManualReset, Name(barId));
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            Diagnostics.BringUpLog.Write(e, "crash guard: could not open the bar's exit intent");
            return null;
        }
    }

    /// <summary>True when the bar declared it was ending on purpose.</summary>
    public static bool WasDeclared(EventWaitHandle? watched) => watched?.WaitOne(0) == true;
}
