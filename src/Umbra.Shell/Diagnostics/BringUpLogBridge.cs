using ManagedShell.Common.Logging;

namespace Umbra.Shell.Diagnostics;

/// <summary>
/// Sends ManagedShell's own log into the bring-up log.
/// </summary>
/// <remarks>
/// <para>
/// <b>ManagedShell reports its internal failures here and then carries on.</b>
/// <c>NotificationArea.SysTrayCallback</c> wraps the whole of its icon handling in
/// <c>catch (Exception ex)</c>, logs, and returns — so a fault there does not surface as a crash,
/// a return value, or a property. It surfaces as nothing at all.
/// </para>
/// <para>
/// That is not hypothetical. A null <c>PinnedNotifyIcons</c> made every single notification icon
/// throw inside that catch, and because no observer was attached the tray simply stayed empty. It
/// was written up as "Windows 11 25H2 no longer routes notification icons", which was wrong, and
/// the mistake survived because the evidence that would have corrected it was being discarded one
/// line before it could be read.
/// </para>
/// <para>
/// Debug is dropped — the tasks service narrates every window activation, which would bury the
/// log. Everything from Info up is kept.
/// </para>
/// </remarks>
internal sealed class BringUpLogBridge : ILog
{
    public void Log(object sender, LogEventArgs e)
    {
        if (e is null || e.Severity < LogSeverity.Info) return;

        if (e.Exception is not null)
        {
            BringUpLog.Write(e.Exception, $"ManagedShell/{e.Severity}: {e.Message}");
            return;
        }

        BringUpLog.Write($"ManagedShell/{e.Severity}: {e.Message}");
    }
}
