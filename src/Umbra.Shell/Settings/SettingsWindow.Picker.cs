using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Umbra.Core.Color;
using Umbra.Shell.Diagnostics;
using Umbra.Shell.Rendering.Themes;

namespace Umbra.Shell.Settings;

/// <summary>The colour picker each swatch opens.</summary>
/// <remarks>
/// <para>
/// <b>A pick goes into the hex box, and only there.</b> The box's own handler saves and applies
/// it, so a picked colour and a typed one take exactly one path, and the box goes on showing the
/// truth while the panel is open.
/// </para>
/// <para>
/// <b>A drag is coalesced, not written on every move.</b> Every write saves <c>settings.txt</c>
/// and re-applies the theme to every open window and the bar, and a drag across the square
/// raises a move per pixel. The box is written at most every <see cref="PickInterval"/> while
/// dragging, and always at once when the drag ends, so the last colour is never lost to the timer.
/// </para>
/// </remarks>
internal partial class SettingsWindow
{
    internal static readonly TimeSpan PickInterval = TimeSpan.FromMilliseconds(60);

    private Popup? _pickerPopup;
    private TextBox? _pickerBox;
    private string? _pickerOriginal;
    private PickQueue? _pickQueue;

    /// <summary>The open picker, for tests.</summary>
    internal ColourPicker? OpenPickerPanel => _pickerPopup?.Child as ColourPicker;

    private void WirePickers()
    {
        Wire(BarAccentSwatch, BarAccentHex, ColourRole.BarAccent, "Bar accent");
        Wire(BarTintSwatch, BarTintHex, ColourRole.RestingTint, "Resting tint");
        Wire(WindowBackgroundSwatch, WindowBackgroundHex, ColourRole.WindowBackground, "Window background");
        Wire(WindowTextSwatch, WindowTextHex, ColourRole.WindowText, "Window text");
        Wire(WindowAccentSwatch, WindowAccentHex, ColourRole.WindowAccent, "Window accent");
        Wire(AppHoverSwatch, AppHoverHex, ColourRole.HoverChip, "Chip colour");

        void Wire(Button swatch, TextBox box, ColourRole role, string title)
        {
            AutomationProperties.SetName(swatch, $"Pick the {title.ToLowerInvariant()}");
            swatch.Click += (_, _) => OpenPicker(swatch, box, role, title);
        }
    }

    /// <summary>Opens the picker under a swatch, starting from its box's colour.</summary>
    internal void OpenPicker(Button swatch, TextBox box, ColourRole role, string title)
    {
        ClosePicker(keep: true);

        // A half-typed box opens on mid grey; the first pick replaces it.
        Rgb start = UmbraTheme.Parse(box.Text, ColourPick.TintPreviewGrey);
        ColourPicker picker = new(role, title, start);

        _pickerBox = box;
        _pickerOriginal = box.Text;

        _pickQueue ??= new PickQueue(WritePick, PickInterval, Dispatcher);
        picker.Picked += (colour, final) => _pickQueue.Queue(colour, final);
        picker.Done += keep => ClosePicker(keep);

        // Right-aligned under the swatch: the swatches sit at the window's right edge, so a
        // panel hanging rightward would run out of the window. WPF flips it above when the
        // window is too near the screen's bottom.
        Popup popup = new()
        {
            Child = picker,
            PlacementTarget = swatch,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [
                new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height + 4), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 4), PopupPrimaryAxis.Horizontal),
            ],
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = SystemParameters.ClientAreaAnimation ? PopupAnimation.Fade : PopupAnimation.None,
        };
        // Closed by a click outside (StaysOpen false) or by ClosePicker; either way once.
        popup.Closed += (_, _) => Finished(popup, refocus: swatch);

        _pickerPopup = popup;
        popup.IsOpen = true;
        BringUpLog.Write($"settings: colour picker opened for {title} at {start.ToHex()}");

        // Into the panel, so Esc and the arrow keys reach it straight away.
        picker.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => picker.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First)));
    }

    /// <summary>Closes the picker; <paramref name="keep"/> false puts the box back as it was.</summary>
    internal void ClosePicker(bool keep)
    {
        if (_pickerPopup is not { } popup) return;

        if (!keep && _pickerBox is { } box && _pickerOriginal is { } original)
        {
            _pickQueue?.Cancel();
            if (box.Text != original) box.Text = original;
        }

        popup.IsOpen = false;
        Finished(popup, refocus: null);
    }

    /// <summary>
    /// Writes any pick still waiting and forgets the panel. Not left to <c>Closed</c> alone: a
    /// popup that never managed to open (its window off screen) raises nothing when closed.
    /// </summary>
    private void Finished(Popup popup, Button? refocus)
    {
        if (!ReferenceEquals(_pickerPopup, popup)) return;

        _pickQueue?.Flush();
        _pickerPopup = null;
        _pickerBox = null;
        _pickerOriginal = null;
        refocus?.Focus();
    }

    private void WritePick(Rgb colour)
    {
        if (_pickerBox is not { } box) return;

        string text = ColourPick.Hex(colour, box.Text);
        if (!string.Equals(text, box.Text.Trim(), StringComparison.OrdinalIgnoreCase)) box.Text = text;
    }
}

/// <summary>
/// Picks on their way to the hex box: a run of moves is written at most once per interval, and
/// a final pick is written at once, taking any move still waiting with it.
/// </summary>
internal sealed class PickQueue(Action<Rgb> write, TimeSpan interval, Dispatcher dispatcher)
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Input, dispatcher) { Interval = interval };
    private Rgb? _pending;
    private bool _wired;

    public void Queue(Rgb colour, bool final)
    {
        _pending = colour;

        if (final)
        {
            Flush();
            return;
        }

        if (!_wired)
        {
            _timer.Tick += (_, _) => Flush();
            _wired = true;
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Writes whatever is waiting, now.</summary>
    public void Flush()
    {
        _timer.Stop();
        if (_pending is not { } colour) return;
        _pending = null;
        write(colour);
    }

    /// <summary>Drops whatever is waiting without writing it.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _pending = null;
    }
}
