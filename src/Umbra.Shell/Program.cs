namespace Umbra.Shell;

/// <summary>
/// The entry point, ahead of WPF's generated one.
/// </summary>
/// <remarks>
/// It exists so that <c>--guard</c> never builds an <see cref="App"/>. The crash guard spends its
/// life idle beside the bar, and a WPF <c>Application</c> it would never use is most of what such a
/// process would otherwise cost. Everything else is exactly what the generated <c>Main</c> does.
/// <c>StartupObject</c> in the project file is what selects this one.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (Shell.CrashGuard.IsGuardLaunch(args)) return Shell.CrashGuard.Run(args);

        App app = new();
        app.InitializeComponent();
        return app.Run();
    }
}
