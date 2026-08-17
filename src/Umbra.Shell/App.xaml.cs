using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Umbra.Core.Layout;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Shell;

namespace Umbra.Shell;

public partial class App : Application
{
    private ShellHost? _host;
    private BarWindow? _bar;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Explorer's taskbar is autohidden for as long as Umbra runs. Every path out of this
        // process has to put it back, including the ones that are not a clean Exit.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _host?.RestoreExplorer();

        BringUpLog.Write("--- startup ---");

        _host = new ShellHost();

        if (_host.RecoveredFromUncleanExit)
            BringUpLog.Write("recovered Explorer taskbar state from a previous unclean exit");

        // Step 1 brings the bar up on the LEFT edge. It is the only edge with no autohide
        // contention from Explorer, so it isolates Umbra's behaviour from Explorer's
        // (shell-integration §2.5). Bottom is step 2, and its contention is a release blocker
        // because bottom is the shipping default.
        BarMetrics metrics = new(BarEdge.Left, Alignment.Start, scale: 1.0);

        _bar = new BarWindow(_host, metrics);
        _bar.Closed += (_, _) => Shutdown();
        _bar.Show();

        LogGeometry(metrics, _bar);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        BringUpLog.Write("--- clean shutdown ---");

        _host?.Dispose();
        _host = null;

        base.OnExit(e);
    }

    private static void LogGeometry(BarMetrics metrics, BarWindow bar)
    {
        Umbra.Core.Layout.Rect reserved = metrics.ReservedRect(
            SystemParameters.PrimaryScreenWidth,
            SystemParameters.PrimaryScreenHeight);

        BringUpLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"edge={metrics.Edge} alignment={metrics.Alignment} dpiScale={bar.DpiScale:F2}"));

        BringUpLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"reserved extent={metrics.ReservedExtent} band={metrics.ContentBand} " +
            $"pitch={metrics.Slot.Pitch} icon={metrics.Slot.IconExtent} rowInset={metrics.RowInset:F2}"));

        // What the spec model computed against what the AppBar actually got. A mismatch here is
        // the first thing worth knowing at step 1, and it is invisible from the core library's
        // own tests.
        BringUpLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"expected rect  x={reserved.X} y={reserved.Y} w={reserved.Width} h={reserved.Height}"));

        BringUpLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"actual   rect  x={bar.Left} y={bar.Top} w={bar.Width} h={bar.Height}"));

        BringUpLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"work area after reservation: {SystemParameters.WorkArea}"));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        BringUpLog.Write(e.Exception, "unhandled dispatcher exception");
        _host?.RestoreExplorer();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            BringUpLog.Write(exception, "unhandled domain exception");

        _host?.RestoreExplorer();
    }
}
