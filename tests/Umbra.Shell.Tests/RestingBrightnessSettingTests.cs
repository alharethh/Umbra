using System.IO;
using System.Windows.Media;
using Umbra.Core.Tiers;
using Umbra.Shell.Rendering;
using Umbra.Shell.Settings;

namespace Umbra.Shell.Tests;

/// <summary>
/// The resting brightness setting: stored as a percentage, refused out of range, live, and never
/// served from a cache filled at another brightness.
/// </summary>
[Collection(RestingBrightnessCollection.Name)]
public class RestingBrightnessSettingTests
{
    [Fact]
    public void It_Defaults_To_The_Table_And_Round_Trips()
    {
        Assert.Equal(100, new UmbraSettings().RestingBrightness);

        string path = Path.Combine(Path.GetTempPath(), $"umbra-brightness-{Guid.NewGuid():N}.txt");
        try
        {
            new UmbraSettings { RestingBrightness = 35 }.Save(path);
            Assert.Equal(35, UmbraSettings.Load(path).RestingBrightness);
            Assert.Equal(35, new UmbraSettings { RestingBrightness = 35 }.Clone().RestingBrightness);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("restingBrightness = 10", 10)]
    [InlineData("restingBrightness = 100", 100)]
    [InlineData("restingBrightness = 9", 100)]     // below the floor: refused, not clamped
    [InlineData("restingBrightness = 101", 100)]   // brighter than the table is never allowed
    [InlineData("restingBrightness = 0", 100)]
    [InlineData("restingBrightness = 50.5", 100)]
    [InlineData("restingBrightness = dim", 100)]
    public void Out_Of_Range_Values_Are_Refused(string line, int expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"umbra-brightness-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(path, [line]);
            Assert.Equal(expected, UmbraSettings.Load(path).RestingBrightness);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void It_Applies_Without_A_Restart()
    {
        Assert.False(new UmbraSettings().NeedsRestartToReach(new UmbraSettings { RestingBrightness = 40 }));
    }

    [Theory]
    [InlineData(100, "100% · no change")]
    [InlineData(50, "50% · 3.2x cooler")]
    [InlineData(10, "10% · 50.1x cooler")]
    public void The_Readout_Says_What_It_Buys(int percent, string expected)
    {
        Assert.Equal(expected, SettingsWindow.BrightnessEffect(percent));
    }

    [Fact]
    public void A_Brush_Cached_At_One_Brightness_Is_Not_Served_At_Another()
    {
        TierBrushConverter converter = new() { Element = BarElement.Clock };
        try
        {
            RestingBrightness.Factor = 1.0;
            Color full = ((SolidColorBrush)converter.Convert(0.0, typeof(Brush), null, null!)).Color;

            RestingBrightness.Factor = 0.25;
            Color dim = ((SolidColorBrush)converter.Convert(0.0, typeof(Brush), null, null!)).Color;

            Assert.Equal(Color.FromRgb(0x33, 0x33, 0x33), full);
            Assert.True(dim.R < full.R, $"#{dim.R:X2} at a quarter against #{full.R:X2} at full");

            // And the full one is still right afterwards.
            RestingBrightness.Factor = 1.0;
            Assert.Equal(full, ((SolidColorBrush)converter.Convert(0.0, typeof(Brush), null, null!)).Color);
        }
        finally { RestingBrightness.Factor = 1.0; }
    }

    /// <summary>
    /// What makes the slider live: the bar raises its drawn tier level with an unchanged value,
    /// and a bound converter has to run again for it.
    /// </summary>
    /// <remarks>
    /// A real binding rather than a call to the converter, because the claim is about WPF: a
    /// dependency property set to its own value raises nothing, and a plain property's change
    /// notification re-reads it and runs the converter whatever the value.
    /// </remarks>
    [Fact]
    public void Raising_An_Unchanged_Level_Redraws_At_The_New_Brightness() => OnSta(() =>
    {
        try
        {
            RestingBrightness.Factor = 1.0;
            LevelSource source = new();
            System.Windows.Controls.TextBlock text = new() { DataContext = source };
            text.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding(nameof(LevelSource.DrawnTierLevel))
                {
                    Converter = new TierBrushConverter { Element = BarElement.Clock },
                });

            Color before = ((SolidColorBrush)text.Foreground).Color;

            RestingBrightness.Factor = 0.25;
            source.Raise();

            Color after = ((SolidColorBrush)text.Foreground).Color;
            Assert.True(after.R < before.R, $"#{after.R:X2} after against #{before.R:X2} before");
        }
        finally { RestingBrightness.Factor = 1.0; }
    });

    private sealed class LevelSource : System.ComponentModel.INotifyPropertyChanged
    {
        public double DrawnTierLevel => 0.0;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void Raise() => PropertyChanged?.Invoke(this, new(nameof(DrawnTierLevel)));
    }

    private static void OnSta(Action body)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try { body(); }
            catch (Exception e) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [Fact]
    public void Engaged_Is_The_Same_At_Any_Brightness()
    {
        TierBrushConverter converter = new() { Element = BarElement.Clock };
        try
        {
            RestingBrightness.Factor = 1.0;
            Color full = ((SolidColorBrush)converter.Convert(2.0, typeof(Brush), null, null!)).Color;

            RestingBrightness.Factor = TierPalette.MinRestingBrightness;
            Assert.Equal(full, ((SolidColorBrush)converter.Convert(2.0, typeof(Brush), null, null!)).Color);
        }
        finally { RestingBrightness.Factor = 1.0; }
    }
}

/// <summary>
/// The resting brightness is process-wide, so tests that change it must not run beside anything
/// that renders through the tier converters.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class RestingBrightnessCollection
{
    public const string Name = "Resting brightness";
}
