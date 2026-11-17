using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Umbra.Core.Color;
using Umbra.Core.Tiers;
using Umbra.Shell.Settings;

namespace Umbra.Shell.Tests;

/// <summary>
/// The colour picker a swatch opens: it must show what will really be drawn, keep its hue
/// through the greys, and write a drag into the hex box without losing the last colour.
/// </summary>
/// <remarks>
/// Nothing here picks inside a real <see cref="SettingsWindow"/>: a pick goes into the hex box,
/// and the box saves the user's real <c>settings.txt</c>. The panel and the queue are tested on
/// their own, and the window only opened and closed untouched.
/// </remarks>
public class ColourPickerTests
{
    private static double L(Rgb c) => Srgb.RelativeLuminance(c);

    [Fact]
    public void The_Bar_Accent_Is_Shown_Capped_As_The_Bar_Will_Draw_It()
    {
        Rgb drawn = ColourPick.Drawn(ColourRole.BarAccent, Rgb.White);

        Assert.NotEqual(Rgb.White, drawn);
        Assert.True(L(drawn) <= TierPalette.Tier2LuminanceCap + 1e-3, $"drawn {drawn} at L={L(drawn)}");
        Assert.Equal(Srgb.CapLuminance(Rgb.White, TierPalette.Tier2LuminanceCap), drawn);
        Assert.Equal(drawn, ColourPick.Drawn(ColourRole.HoverChip, Rgb.White));
    }

    [Fact]
    public void A_Tint_Is_Shown_At_Its_Own_Hue_And_The_Bars_Brightness()
    {
        // A dark blue: the tint takes its hue only, so the preview is a full-strength resting blue
        // at the grey's own luminance, nothing like the near-black that was picked.
        Rgb picked = Rgb.FromHex("#101040");
        Rgb drawn = ColourPick.Drawn(ColourRole.RestingTint, picked);

        Assert.Equal(RestingTint.Toward(picked).Apply(ColourPick.TintPreviewGrey), drawn);
        Assert.Equal(L(ColourPick.TintPreviewGrey), L(drawn), 2);
        Assert.True(drawn.B > drawn.R, $"{drawn} is not blue");
    }

    [Theory]
    [InlineData("WindowBackground")]
    [InlineData("WindowText")]
    [InlineData("WindowAccent")]
    public void Window_Colours_Are_Drawn_As_Picked(string name)
    {
        ColourRole role = Enum.Parse<ColourRole>(name);
        Assert.Equal(Rgb.White, ColourPick.Drawn(role, Rgb.White));
        Assert.Null(ColourPick.DrawnLabel(role));
    }

    [Theory]
    [InlineData("#53E7FF", "#123456", "#123456")]
    [InlineData("#80FFFFFF", "#123456", "#80123456")]
    [InlineData(" #ff1f1f1a ", "#ABCDEF", "#FFABCDEF")]
    [InlineData("#53E", "#123456", "#123456")]
    public void A_Pick_Is_Written_As_Hex_Keeping_Any_Alpha(string previous, string picked, string expected) =>
        Assert.Equal(expected, ColourPick.Hex(Rgb.FromHex(picked), previous));

    [Theory]
    [InlineData("BarAccent")]
    [InlineData("RestingTint")]
    [InlineData("WindowBackground")]
    [InlineData("WindowText")]
    [InlineData("WindowAccent")]
    [InlineData("HoverChip")]
    public void Presets_Start_With_The_Default_Themes_Colour_And_Fit_Two_Rows(string name)
    {
        ColourRole role = Enum.Parse<ColourRole>(name);
        IReadOnlyList<Rgb> presets = ColourPick.Presets(role);
        Assert.InRange(presets.Count, 6, 12);
        Assert.Equal(presets.Count, presets.Distinct().Count());
        Rgb expected = Rgb.FromHex(role switch
        {
            ColourRole.BarAccent => "#53E7FF",
            ColourRole.RestingTint => "#808080",
            ColourRole.WindowBackground => "#14140F",
            ColourRole.WindowText => "#B6B6AD",
            ColourRole.WindowAccent => "#B0F7FB",
            _ => UmbraSettings.DefaultAppIconHoverColour,
        });
        Assert.Equal(expected, presets[0]);
    }

    [Fact]
    public void The_Panel_Shows_The_Pick_And_What_Is_Drawn() => OnSta(() =>
    {
        ColourPicker picker = new(ColourRole.BarAccent, "Bar accent", Rgb.FromHex("#53E7FF"));
        List<(Rgb, bool)> picks = [];
        picker.Picked += (c, final) => picks.Add((c, final));

        picker.PickPreset(Rgb.White);

        Assert.Equal(Rgb.White, picker.PickedShown);
        Assert.Equal(ColourPick.Drawn(ColourRole.BarAccent, Rgb.White), picker.DrawnShown);
        Assert.Equal([(Rgb.White, true)], picks);
    });

    [Fact]
    public void The_Hue_Is_Kept_Through_The_Greys() => OnSta(() =>
    {
        ColourPicker picker = new(ColourRole.BarAccent, "Bar accent", Rgb.FromHex("#53E7FF"));
        picker.PickHue(200, final: false);

        // Into the grey column and down to black: no hue to read back, so the strip must hold.
        picker.PickSquare(0, 0.5, final: false);
        Assert.Equal(200, picker.Hue, 3);
        picker.PickSquare(0.5, 0, final: false);
        Assert.Equal(200, picker.Hue, 3);

        // Back out of it, the colour is the hue it left with.
        picker.PickSquare(1, 1, final: true);
        Assert.Equal(200, Hsv.FromRgb(picker.Colour).H, 0);

        // A grey preset leaves the hue alone too.
        picker.PickPreset(Rgb.FromHex("#808080"));
        Assert.Equal(200, picker.Hue, 3);
    });

    [Fact]
    public void A_Drag_Is_Written_Once_An_Interval_And_Its_End_At_Once() => OnSta(() =>
    {
        List<Rgb> written = [];
        PickQueue queue = new(written.Add, TimeSpan.FromMilliseconds(60), Dispatcher.CurrentDispatcher);

        for (int i = 0; i < 20; i++) queue.Queue(new Rgb((byte)i, 0, 0), final: false);
        Assert.Empty(written);

        Pump(TimeSpan.FromMilliseconds(200));
        Assert.Equal([new Rgb(19, 0, 0)], written);

        // The end of a drag: written at once, without waiting on the timer.
        queue.Queue(new Rgb(30, 0, 0), final: false);
        queue.Queue(new Rgb(31, 0, 0), final: true);
        Assert.Equal([new Rgb(19, 0, 0), new Rgb(31, 0, 0)], written);

        // Nothing left over for the timer to write again.
        Pump(TimeSpan.FromMilliseconds(200));
        Assert.Equal(2, written.Count);
    });

    [Fact]
    public void A_Cancelled_Drag_Writes_Nothing() => OnSta(() =>
    {
        List<Rgb> written = [];
        PickQueue queue = new(written.Add, TimeSpan.FromMilliseconds(60), Dispatcher.CurrentDispatcher);

        queue.Queue(Rgb.White, final: false);
        queue.Cancel();
        Pump(TimeSpan.FromMilliseconds(200));

        // Closing the panel flushes; after Esc that must not write the undone pick back.
        queue.Flush();

        Assert.Empty(written);
    });

    [Fact]
    public void A_Swatch_Opens_The_Picker_On_Its_Boxs_Colour_And_Esc_Leaves_The_Box_Alone() => OnSta(() =>
    {
        UmbraSettings settings = new();
        SettingsWindow window = new(settings, _ => { }, () => { })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            string before = window.BarAccentHex.Text;
            window.OpenPicker(window.BarAccentSwatch, window.BarAccentHex, ColourRole.BarAccent, "Bar accent");

            ColourPicker? panel = window.OpenPickerPanel;
            Assert.NotNull(panel);
            Assert.Equal(Rgb.FromHex(before), panel!.Colour);
            Assert.Equal(ColourPick.Drawn(ColourRole.BarAccent, Rgb.FromHex(before)), panel.DrawnShown);

            window.ClosePicker(keep: false);
            Pump(TimeSpan.FromMilliseconds(50));

            Assert.Null(window.OpenPickerPanel);
            Assert.Equal(before, window.BarAccentHex.Text);
        }
        finally { window.Close(); }
    });

    private static void Pump(TimeSpan time)
    {
        DispatcherFrame frame = new();
        DispatcherTimer stop = new(DispatcherPriority.Background) { Interval = time };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void OnSta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
