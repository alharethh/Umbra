using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Umbra.Core.Tiers;

namespace Umbra.Shell.Tiers;

/// <summary>
/// Drives the tier level from pointer proximity to the bar (design-spec §3, §3.4).
/// </summary>
/// <remarks>
/// <para>
/// The pointer has to be tracked while it is <b>outside</b> the window — Tier 1 begins 120 px away
/// — so WPF's mouse events are no use and the cursor is polled instead.
/// </para>
/// <para>
/// The tick rate drops to <see cref="IdleIntervalMs"/> once the bar has settled at Tier 0, which is
/// where it spends ~92% of its life. That is not micro-optimisation: §6.6 turns on the display
/// being allowed to idle so the panel can run its own compensation cycles, and a timer that never
/// slows down is one more thing keeping it awake. ManagedShell already contributes two 100 ms
/// timers we do not control (§11.2 Q2); ours should not be a third.
/// </para>
/// </remarks>
internal sealed class ProximityDriver : IDisposable
{
    /// <summary>Tick interval while anything is moving or lit.</summary>
    private const int ActiveIntervalMs = 16;

    /// <summary>Tick interval once settled at Tier 0 — enough to notice the pointer approaching.</summary>
    private const int IdleIntervalMs = 100;

    private readonly DispatcherTimer _timer;
    private readonly TierController _controller;
    private readonly Func<Rect> _barRect;
    private readonly Func<double> _dpiScale;
    private DateTime _lastTick = DateTime.UtcNow;
    private DateTime _lastLog = DateTime.MinValue;
    private double _lastLoggedLevel = double.NaN;

    /// <param name="engagedBandPixels">
    /// How close the pointer must be for the bar to engage. Zero means "over the bar"; null takes
    /// §3's 40 px band.
    /// </param>
    public ProximityDriver(Func<Rect> barRect, Func<double> dpiScale, double scale = 1.0,
                           double? engagedBandPixels = null)
    {
        _barRect = barRect;
        _dpiScale = dpiScale;
        _controller = new TierController(scale, engagedBandPixels);

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(ActiveIntervalMs),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>Raised when the tier level changes enough to be worth repainting.</summary>
    public event Action<double>? LevelChanged;

    /// <summary>
    /// Raised on every tick. For anything that needs polling but does not deserve its own timer —
    /// §6.6 turns on not adding wake-ups, and this one is already running.
    /// </summary>
    public event Action? Ticked;

    /// <summary>Current continuous tier level, 0..2.</summary>
    public double Level => _controller.Level;

    /// <summary>Distance from the pointer to the bar, in device-independent pixels.</summary>
    public double Distance { get; private set; } = double.MaxValue;

    /// <summary>
    /// Holds the tick at the fast interval regardless of pointer distance, for callers that need
    /// prompt polling while something transient is on screen.
    /// </summary>
    /// <remarks>
    /// Deliberately opt-in and expected to be switched off again. §6.6 turns on the display being
    /// allowed to idle, so a permanent fast tick is exactly what this class exists to avoid.
    /// </remarks>
    public bool KeepFast { get; set; }

    /// <summary>
    /// Whether the pointer is on a panel the bar itself opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A panel of ours is part of the bar as far as the tier system is concerned.</b> The
    /// overflow flyout, the window picker and the context menus all open <i>off</i> the bar, so
    /// reaching for one takes the pointer outside the bar's rectangle — the distance jumps, the
    /// level ramps down, and the icons grey out underneath the panel the user is trying to use.
    /// Reported exactly that way: "the mouse moves up which triggers the bar to go idle".
    /// </para>
    /// <para>
    /// Supplied as a predicate rather than as extra rectangles because the owner already knows how
    /// to test each panel, and each of those tests is written in the units that panel reports —
    /// physical pixels for a popup's window rectangle, device-independent ones for the picker. A
    /// rectangle handed over here would have to be converted, and mixing those two is the bug this
    /// project keeps rediscovering.
    /// </para>
    /// </remarks>
    public Func<bool>? PointerOverPanel { get; set; }

    public void Start() => _timer.Start();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        // A long gap means the machine slept or the timer was starved. Feeding that straight into
        // the controller would jump the level rather than ramp it.
        elapsed = Math.Clamp(elapsed, 0.0, 0.25);

        double previous = _controller.Level;
        Distance = DistanceToBar();
        _controller.Update(Distance, elapsed);

        // Only while something is actually changing — a bar sitting at Tier 0 for hours should not
        // be writing to disk twice a second.
        if (Math.Abs(_controller.Level - _lastLoggedLevel) > 0.01 && (now - _lastLog).TotalMilliseconds >= 250)
        {
            _lastLog = now;
            _lastLoggedLevel = _controller.Level;
            System.Windows.Rect bar = _barRect();
            Diagnostics.BringUpLog.Write(
                System.String.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"proximity: bar=({bar.Left:F0},{bar.Top:F0},{bar.Width:F0}x{bar.Height:F0}) " +
                    $"dpi={_dpiScale():F2} distance={Distance:F1} level={_controller.Level:F3}"));
        }

        if (Math.Abs(_controller.Level - previous) > 1e-6)
            LevelChanged?.Invoke(_controller.Level);

        Ticked?.Invoke();

        bool settledAtRest = !KeepFast && _controller.Level <= 1e-6 && Distance >= _controller.AmbientBand;
        _timer.Interval = TimeSpan.FromMilliseconds(settledAtRest ? IdleIntervalMs : ActiveIntervalMs);
    }

    private double DistanceToBar()
    {
        // On one of our own panels counts as on the bar, at zero distance — see PointerOverPanel.
        if (PointerOverPanel?.Invoke() == true) return 0.0;

        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return double.MaxValue;

        // GetCursorPos is in physical pixels; the bar's rect is in DIPs. On a mixed-DPI setup this
        // conversion is only right for the bar's own monitor, which is the only one it can be lit
        // from anyway.
        double scale = _dpiScale();
        if (scale <= 0.0) scale = 1.0;

        double x = cursor.X / scale;
        double y = cursor.Y / scale;

        Rect bar = _barRect();
        double dx = Math.Max(0.0, Math.Max(bar.Left - x, x - bar.Right));
        double dy = Math.Max(0.0, Math.Max(bar.Top - y, y - bar.Bottom));

        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT lpPoint);
    }
}
