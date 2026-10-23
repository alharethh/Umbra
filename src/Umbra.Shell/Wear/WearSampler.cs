using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shape = System.Windows.Shapes.Shape;
using Microsoft.Win32;
using Umbra.Core.Color;
using Umbra.Core.Wear;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Wear;

/// <summary>One drawn element and where it landed, in device-independent pixels.</summary>
internal readonly record struct WearEmitter(LuminanceField Field, Rect Bounds);

/// <summary>
/// Accumulates per-pixel wear over the reserved rectangle (design-spec §9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 1 is instrumentation only.</b> Accumulate, persist, draw a heatmap. Nothing here feeds
/// back into layout, and nothing should until several weeks of real data have been looked at — a
/// feedback loop built on an unvalidated model produces confident nonsense that cannot be told
/// apart from insight.
/// </para>
/// <para>
/// The scene is read from the visual tree and its transforms rather than by reading back the
/// framebuffer. §9 asks for that because it is cheaper and exact, and it does not stall the
/// pipeline once a second — and because it keeps working when the content group acquires a drift
/// transform in step 5, which <see cref="System.Windows.UIElement.TransformToAncestor"/> folds in
/// for free.
/// </para>
/// <para>
/// Time spent locked, idle or with the display off is added to the wall clock but accumulates no
/// wear, so the denominator stays honest across a machine that sleeps (§6.6).
/// </para>
/// </remarks>
internal sealed class WearSampler : IDisposable
{
    /// <summary>Sample rate from §9.</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>Persist interval from §9.</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(5);

    private readonly DispatcherTimer _timer;
    private readonly Func<IReadOnlyList<WearEmitter>> _scene;
    private readonly Func<double> _dpiScale;
    private readonly string _path;
    private readonly double[] _frame;

    private WearAccumulator _accumulator;
    private DateTime _lastSample = DateTime.UtcNow;
    private DateTime _lastPersist = DateTime.UtcNow;
    private bool _suspended;

    public WearSampler(
        int widthPhysical,
        int heightPhysical,
        Func<IReadOnlyList<WearEmitter>> scene,
        Func<double> dpiScale,
        string? path = null)
    {
        _scene = scene;
        _dpiScale = dpiScale;
        _path = path ?? DefaultPath;

        _accumulator = LoadOrCreate(widthPhysical, heightPhysical, _path);
        _frame = new double[widthPhysical * heightPhysical];

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SampleInterval };
        _timer.Tick += OnTick;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Where the dataset lives.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Umbra",
        "wear.dat");

    /// <summary>
    /// Where a particular screen's dataset lives, when there is a bar on more than one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One file per screen, and it is not optional.</b> The accumulator is sized to the
    /// reserved rectangle in physical pixels, so two screens of different size or orientation are
    /// two incompatible grids. <c>WearStore.LoadCompatible</c> rightly refuses to load one into
    /// the other — and then the bar that refused it saves over it, so both datasets are lost, one
    /// per restart, silently.
    /// </para>
    /// <para>
    /// The primary keeps the plain <c>wear.dat</c> so a single-monitor user's history survives
    /// turning the setting on and off. Others are keyed by device name with the characters a
    /// path cannot carry replaced — <c>\.\DISPLAY2</c> is not a file name.
    /// </para>
    /// </remarks>
    public static string PathForScreen(string? deviceName, bool primary)
    {
        if (primary || string.IsNullOrWhiteSpace(deviceName)) return DefaultPath;

        System.Text.StringBuilder safe = new(deviceName.Length);

        foreach (char c in deviceName)
            safe.Append(char.IsLetterOrDigit(c) ? c : '-');

        return Path.Combine(
            Path.GetDirectoryName(DefaultPath)!,
            $"wear-{safe}.dat");
    }

    /// <summary>The accumulated data, for the heatmap view.</summary>
    public WearAccumulator Accumulator => _accumulator;

    /// <summary>
    /// Stops wear being credited while still advancing the clock, exactly as a session lock does.
    /// </summary>
    /// <remarks>
    /// Set while the motion speed multiplier is above 1×. Simulated drift moves content hundreds of
    /// times faster than reality, so anything sampled during it would be a fiction written into a
    /// dataset whose whole purpose is to answer §11.2 Q1 from real evidence. Better a gap in the
    /// record than an invented stretch of it.
    /// <para>
    /// <b>Debug builds only</b>, like the slider that is its one caller. <c>_suspended</c> itself
    /// stays in every build, because a session lock sets it too.
    /// </para>
    /// </remarks>
#if DEBUG
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            if (value) Persist();
        }
    }
#endif

    public void Start()
    {
        _lastSample = DateTime.UtcNow;
        _timer.Start();
    }

    /// <summary>Writes the dataset out. Called every five minutes, on session lock, and on exit.</summary>
    public void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Write beside the target and swap, so a crash mid-write cannot destroy weeks of data.
            string temporary = _path + ".tmp";
            using (FileStream stream = File.Create(temporary))
            {
                WearStore.Save(_accumulator, stream);
            }

            File.Move(temporary, _path, overwrite: true);
            _lastPersist = DateTime.UtcNow;
        }
        catch (IOException e)
        {
            BringUpLog.Write(e, "wear persist failed");
        }
        catch (UnauthorizedAccessException e)
        {
            BringUpLog.Write(e, "wear persist failed");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        Persist();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = (now - _lastSample).TotalSeconds;
        _lastSample = now;

        // A gap far longer than the interval means the machine slept or the timer was starved;
        // crediting all of it as lit time would overstate the wear substantially.
        elapsed = Math.Clamp(elapsed, 0.0, SampleInterval.TotalSeconds * 4);

        if (_suspended)
        {
            _accumulator.Idle(elapsed);
        }
        else
        {
            Composite();
            _accumulator.AddFrame(_frame, elapsed);
        }

        if (now - _lastPersist >= PersistInterval) Persist();
    }

    private void Composite() =>
        SceneCompositor.Composite(_scene(), _frame, _accumulator.Width, _accumulator.Height, _dpiScale());

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _suspended = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff;
        if (_suspended) Persist();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) { _suspended = true; Persist(); }
        else if (e.Mode == PowerModes.Resume) _suspended = false;
    }

    private static WearAccumulator LoadOrCreate(int width, int height, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using FileStream stream = File.OpenRead(path);
                WearAccumulator? loaded = WearStore.LoadCompatible(
                    stream, width, height, WearModel.DefaultExponent);

                if (loaded is not null)
                {
                    BringUpLog.Write(
                        $"wear dataset resumed: {loaded.ElapsedSeconds / 3600.0:F1}h, total {loaded.Total:G6}");
                    return loaded;
                }

                BringUpLog.Write("wear dataset discarded: geometry or exponent changed");
            }
        }
        catch (IOException e)
        {
            BringUpLog.Write(e, "wear load failed");
        }

        return new WearAccumulator(width, height);
    }
}

/// <summary>Reads the drawn scene out of a visual tree.</summary>
internal static class SceneReader
{
    // Weak, for the reason IconPipeline's cache is: every tray icon update draws a new bitmap, and
    // a dictionary held each one it sampled, with its field, for the life of the bar.
    private static readonly ConditionalWeakTable<ImageSource, LuminanceField> IconFields = [];
    private static readonly Dictionary<System.Windows.Media.Color, LuminanceField> SolidFields = [];

    /// <summary>
    /// Collects every lit element under <paramref name="root"/>, with bounds transformed into
    /// <paramref name="reference"/>'s coordinates — which folds in any render transform on the way
    /// up, including the drift transform once §6 lands.
    /// </summary>
    public static List<WearEmitter> Read(DependencyObject root, UIElement reference)
    {
        List<WearEmitter> emitters = [];
        Collect(root, reference, emitters);
        return emitters;
    }

    private static void Collect(DependencyObject node, UIElement reference, List<WearEmitter> emitters)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);

            switch (child)
            {
                case Image { Source: BitmapSource bitmap } image when image.IsVisible:
                    Add(emitters, image, reference, IconField(bitmap));
                    break;

                case Shape { Fill: SolidColorBrush brush } shape when shape.IsVisible:
                    Add(emitters, shape, reference, SolidField(brush.Color));
                    break;
            }

            Collect(child, reference, emitters);
        }
    }

    private static void Add(List<WearEmitter> emitters, FrameworkElement element,
                            UIElement reference, LuminanceField field)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;

        try
        {
            GeneralTransform transform = element.TransformToAncestor(reference);
            Rect bounds = transform.TransformBounds(
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));

            emitters.Add(new WearEmitter(field, bounds));
        }
        catch (InvalidOperationException)
        {
            // Not currently connected to the reference element; it drew nothing this second.
        }
    }

    internal static LuminanceField IconField(BitmapSource bitmap)
    {
        return IconFields.GetValue(bitmap, static source => LuminanceField.FromBitmap((BitmapSource)source));
    }

    private static LuminanceField SolidField(System.Windows.Media.Color colour)
    {
        if (SolidFields.TryGetValue(colour, out LuminanceField? cached)) return cached;

        LuminanceField field = colour.A == 0
            ? LuminanceField.Dark
            : LuminanceField.Solid(new Rgb(colour.R, colour.G, colour.B));

        SolidFields[colour] = field;
        return field;
    }
}
