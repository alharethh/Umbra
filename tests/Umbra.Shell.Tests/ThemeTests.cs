using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Umbra.Core.Color;
using Umbra.Core.Tiers;
using Umbra.Shell.Rendering;
using Umbra.Shell.Rendering.Themes;
using Umbra.Shell.Settings;

namespace Umbra.Shell.Tests;

/// <summary>
/// Themes: curated sets of five colours for the bar and Umbra's windows, and the derived palette.
/// </summary>
[Collection(RestingBrightnessCollection.Name)]
public class ThemeTests
{
    private static double L(Rgb c) => Srgb.RelativeLuminance(c);

    private static double Contrast(Rgb a, Rgb b)
    {
        double la = L(a), lb = L(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// The default theme is what Umbra drew before themes, to within a code value: the steps were
    /// measured off those hand-picked colours.
    /// </summary>
    [Theory]
    [InlineData(nameof(WindowPalette.Background), "#14140F")]
    [InlineData(nameof(WindowPalette.Surface), "#1B1B18")]
    [InlineData(nameof(WindowPalette.NavHover), "#1E1E19")]
    [InlineData(nameof(WindowPalette.Control), "#23231E")]
    [InlineData(nameof(WindowPalette.NavSelected), "#26261F")]
    [InlineData(nameof(WindowPalette.ControlHover), "#2C2C25")]
    [InlineData(nameof(WindowPalette.Divider), "#2E2E28")]
    [InlineData(nameof(WindowPalette.Highlight), "#33332B")]
    [InlineData(nameof(WindowPalette.Border), "#3A3A33")]
    [InlineData(nameof(WindowPalette.Thumb), "#46463E")]
    [InlineData(nameof(WindowPalette.BorderHover), "#4A4A42")]
    [InlineData(nameof(WindowPalette.TextDim), "#6E6E66")]
    [InlineData(nameof(WindowPalette.TextHint), "#8A8A82")]
    [InlineData(nameof(WindowPalette.Glyph), "#9A9A90")]
    [InlineData(nameof(WindowPalette.Text), "#B6B6AD")]
    [InlineData(nameof(WindowPalette.TextItem), "#E0E0D8")]
    [InlineData(nameof(WindowPalette.TextStrong), "#E8E4DA")]
    [InlineData(nameof(WindowPalette.Accent), "#B0F7FB")]
    public void The_Default_Theme_Draws_What_Umbra_Always_Drew(string part, string handPicked)
    {
        WindowPalette palette = UmbraTheme.Palette(UmbraTheme.Default.Colours);
        Rgb derived = (Rgb)typeof(WindowPalette).GetProperty(part)!.GetValue(palette)!;
        Rgb expected = Rgb.FromHex(handPicked);

        // TextStrong was hand-tuned a little less green than any one mix reaches.
        int tolerance = part == nameof(WindowPalette.TextStrong) ? 4 : 3;

        Assert.True(Math.Abs(derived.R - expected.R) <= tolerance
                    && Math.Abs(derived.G - expected.G) <= tolerance
                    && Math.Abs(derived.B - expected.B) <= tolerance,
            $"{part}: derived {derived}, hand-picked {expected}");
    }

    [Fact]
    public void The_Default_Theme_Does_Not_Tint_The_Bar() =>
        Assert.True(UmbraTheme.TintOf(UmbraTheme.Default.Colours).IsNone);

    [Fact]
    public void The_Default_Theme_Is_The_Default_Settings()
    {
        Assert.True(new UmbraSettings().Theme.SameAs(UmbraTheme.Default.Colours));
        Assert.Equal(UmbraSettings.DefaultHoverAccent, UmbraTheme.Default.Colours.BarAccent);
    }

    /// <summary>
    /// Every theme is dark and readable. Dark because a light window beside a black bar would be
    /// the brightest thing on an OLED panel; readable by WCAG's numbers, 7:1 for text (AAA),
    /// 4.5:1 for the hints, 3:1 for the accent, which marks things rather than spelling them.
    /// </summary>
    [Fact]
    public void Every_Theme_Is_Dark_And_Readable()
    {
        foreach (ThemePreset preset in UmbraTheme.Presets)
        {
            WindowPalette p = UmbraTheme.Palette(preset.Colours);

            Assert.True(L(p.Background) < 0.01, $"{preset.Name}: background L {L(p.Background):F4}");
            Assert.True(Contrast(p.Text, p.Background) >= 7.0, $"{preset.Name}: text {Contrast(p.Text, p.Background):F1}:1");
            Assert.True(Contrast(p.TextHint, p.Background) >= 4.5, $"{preset.Name}: hint {Contrast(p.TextHint, p.Background):F1}:1");
            Assert.True(Contrast(p.Accent, p.Background) >= 3.0, $"{preset.Name}: accent {Contrast(p.Accent, p.Background):F1}:1");
            Assert.True(Contrast(p.TextItem, p.Surface) >= 7.0, $"{preset.Name}: menu text {Contrast(p.TextItem, p.Surface):F1}:1");
        }
    }

    [Fact]
    public void Every_Theme_Has_Its_Own_Name_And_Colours()
    {
        Assert.Equal(UmbraTheme.Presets.Count, UmbraTheme.Presets.Select(p => p.Name).Distinct().Count());

        foreach (ThemePreset a in UmbraTheme.Presets)
        foreach (ThemePreset b in UmbraTheme.Presets)
            if (!ReferenceEquals(a, b)) Assert.False(a.Colours.SameAs(b.Colours), $"{a.Name} = {b.Name}");

        // Every theme but the default gives the bar a hue; that is most of what makes it a theme.
        foreach (ThemePreset preset in UmbraTheme.Presets.Skip(1))
            Assert.False(UmbraTheme.TintOf(preset.Colours).IsNone, preset.Name);
    }

    [Fact]
    public void Colours_Match_A_Theme_Regardless_Of_Case_And_Otherwise_Are_Custom()
    {
        ThemeColours ember = UmbraTheme.Presets.Single(p => p.Name == "Ember").Colours;

        Assert.Equal("Ember", UmbraTheme.Matching(ember with { BarAccent = ember.BarAccent.ToLowerInvariant() })?.Name);
        Assert.Null(UmbraTheme.Matching(ember with { WindowText = "#FFFFFF" }));
    }

    [Fact]
    public void An_Unreadable_Colour_Falls_Back_Rather_Than_Throwing()
    {
        ThemeColours broken = UmbraTheme.Default.Colours with { WindowBackground = "#nothex" };
        Assert.Equal(Rgb.FromHex("#14140F"), UmbraTheme.Palette(broken).Background);
        Assert.Equal(Rgb.FromHex("#112233"), UmbraTheme.Parse("#FF112233", Rgb.Black));
    }

    [Fact]
    public void The_Theme_Round_Trips_Through_The_File()
    {
        ThemeColours dusk = UmbraTheme.Presets.Single(p => p.Name == "Dusk").Colours;
        string path = Path.Combine(Path.GetTempPath(), $"umbra-theme-{Guid.NewGuid():N}.txt");
        try
        {
            new UmbraSettings { Theme = dusk }.Save(path);
            UmbraSettings read = UmbraSettings.Load(path);

            Assert.True(read.Theme.SameAs(dusk));
            Assert.True(read.Clone().Theme.SameAs(dusk));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_Bad_Theme_Colour_In_The_File_Is_Ignored()
    {
        string path = Path.Combine(Path.GetTempPath(), $"umbra-theme-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(path, ["windowBackground = black", "barTint = #12345", "windowAccent = #AABBCC"]);
            UmbraSettings read = UmbraSettings.Load(path);

            Assert.Equal(UmbraTheme.Default.Colours.WindowBackground, read.WindowBackground);
            Assert.Equal(UmbraTheme.Default.Colours.BarTint, read.BarTint);
            Assert.Equal("#AABBCC", read.WindowAccent);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The tint reaches the drawn greys, at their luminance, and a cached brush from before a
    /// theme change is not served after it.
    /// </summary>
    [Fact]
    public void The_Clock_Takes_The_Tint_At_The_Same_Luminance()
    {
        TierBrushConverter converter = new() { Element = BarElement.Clock };
        try
        {
            RestingBrightness.Tint = RestingTint.None;
            Color plain = ((SolidColorBrush)converter.Convert(1.0, typeof(Brush), null, null!)).Color;

            RestingBrightness.Tint = UmbraTheme.TintOf(UmbraTheme.Presets.Single(p => p.Name == "Ember").Colours);
            Color warm = ((SolidColorBrush)converter.Convert(1.0, typeof(Brush), null, null!)).Color;

            Assert.Equal(Color.FromRgb(0x40, 0x40, 0x40), plain);
            Assert.True(warm.R > warm.B, $"Ember drew #{warm.R:X2}{warm.G:X2}{warm.B:X2}");
            Assert.Equal(L(new Rgb(0x40, 0x40, 0x40)), L(new Rgb(warm.R, warm.G, warm.B)), 3);
        }
        finally { RestingBrightness.Tint = RestingTint.None; }
    }

    [Fact]
    public void The_Badge_Is_Never_Tinted()
    {
        TierBrushConverter converter = new() { Element = BarElement.NotificationBadge };
        try
        {
            RestingBrightness.Tint = UmbraTheme.TintOf(UmbraTheme.Presets.Single(p => p.Name == "Rose").Colours);
            Color badge = ((SolidColorBrush)converter.Convert(0.0, typeof(Brush), null, null!)).Color;
            Assert.Equal(Color.FromRgb(0xB3, 0xB3, 0xB3), badge);
        }
        finally { RestingBrightness.Tint = RestingTint.None; }
    }

    [Fact]
    public void The_Settings_Window_Shows_The_Matching_Theme() => OnSta(() =>
    {
        ThemeColours moss = UmbraTheme.Presets.Single(p => p.Name == "Moss").Colours;
        SettingsWindow window = Shown(new UmbraSettings { Theme = moss });
        try
        {
            Assert.Equal("Moss", (window.ThemeList.SelectedItem as ListBoxItem)?.Tag is ThemePreset p ? p.Name : null);
            Assert.Equal(Visibility.Hidden, window.ThemeCustomNote.Visibility);
            Assert.True(window.ChosenTheme().SameAs(moss));
            Assert.Equal(UmbraTheme.Presets.Count, window.ThemeList.Items.Count);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// A hover accent set by hand before themes existed opens as Custom with that accent kept,
    /// rather than being replaced by the default theme's.
    /// </summary>
    [Fact]
    public void A_Hand_Set_Accent_Opens_As_Custom_And_Is_Kept() => OnSta(() =>
    {
        SettingsWindow window = Shown(new UmbraSettings { HoverAccent = "#FFC24C" });
        try
        {
            Assert.Null(window.ThemeList.SelectedItem);
            Assert.Equal(Visibility.Visible, window.ThemeCustomNote.Visibility);
            Assert.Equal("#FFC24C", window.ChosenTheme().BarAccent);
        }
        finally { window.Close(); }
    });

    private static SettingsWindow Shown(UmbraSettings settings)
    {
        var window = new SettingsWindow(settings, _ => { }, () => { })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        return window;
    }

    private static void OnSta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
