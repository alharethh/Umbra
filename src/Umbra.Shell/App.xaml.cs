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

        // Bottom is the shipping default (§7.1) and the only edge that contends with Explorer's
        // autohidden taskbar (shell-integration §2.5). Left is the control: it is the one edge
        // where none of that contention exists, so running the two back to back is what makes the
        // contention attributable rather than merely observed.
        BarEdge edge = ParseEdge(e.Args);
        BarMetrics metrics = new(edge, Alignment.Start, scale: 1.0);

        bool motion = !e.Args.Contains("--no-motion", StringComparer.OrdinalIgnoreCase);

        _bar = new BarWindow(_host, metrics) { MotionEnabled = motion };
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

    /// <summary>
    /// <c>--edge left|top|right|bottom</c>. Defaults to bottom, the shipping default (§7.1).
    /// </summary>
    private static BarEdge ParseEdge(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--edge", StringComparison.OrdinalIgnoreCase)) continue;
            if (Enum.TryParse(args[i + 1], ignoreCase: true, out BarEdge parsed)) return parsed;

            BringUpLog.Write($"unrecognised --edge value '{args[i + 1]}', falling back to Bottom");
        }

        return BarEdge.Bottom;
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
