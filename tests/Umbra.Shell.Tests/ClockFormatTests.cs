using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using Umbra.Shell.Rendering;
using Umbra.Shell.Settings;

namespace Umbra.Shell.Tests;

/// <summary>
/// The clock's 12 or 24 hours: the setting, what the time line says, and the width it reserves.
/// </summary>
public class ClockFormatTests
{
    private static readonly DateTime Afternoon = new(2026, 11, 14, 14, 5, 9);
    private static readonly DateTime Morning = new(2026, 11, 14, 9, 30, 0);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData("h:mm tt", true)]
    [InlineData("hh:mm", true)]
    [InlineData("HH:mm", false)]
    [InlineData("H:mm", false)]
    [InlineData("'h'H:mm", false)]   // a quoted h is a literal, not an hour
    [InlineData("\\hH:mm", false)]   // so is an escaped one
    [InlineData("tt h:mm", true)]
    public void A_Lowercase_H_Outside_Quotes_Is_A_Twelve_Hour_Pattern(string pattern, bool twelve) =>
        Assert.Equal(twelve, ClockText.PatternIsTwelveHour(pattern));

    [Fact]
    public void Follow_Windows_Reads_The_Cultures_Short_Time_Pattern()
    {
        var twelve = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
        twelve.DateTimeFormat.ShortTimePattern = "h:mm tt";
        var twentyFour = (CultureInfo)English.Clone();
        twentyFour.DateTimeFormat.ShortTimePattern = "HH:mm";

        Assert.True(ClockText.IsTwelveHour(ClockFormat.Windows, twelve));
        Assert.False(ClockText.IsTwelveHour(ClockFormat.Windows, twentyFour));

        // The explicit choices ignore the culture entirely.
        Assert.True(ClockText.IsTwelveHour(ClockFormat.TwelveHour, twentyFour));
        Assert.False(ClockText.IsTwelveHour(ClockFormat.TwentyFourHour, twelve));
    }

    [Fact]
    public void The_Time_Line_Says_What_Each_Format_Should()
    {
        Assert.Equal("14:05", ClockText.Time(Afternoon, false, false, false, English));
        Assert.Equal("14:05:09", ClockText.Time(Afternoon, false, true, false, English));
        Assert.Equal("2:05 PM", ClockText.Time(Afternoon, true, false, false, English));
        Assert.Equal("2:05:09 PM", ClockText.Time(Afternoon, true, true, false, English));
        Assert.Equal("9:30 AM", ClockText.Time(Morning, true, false, false, English));
    }

    [Fact]
    public void A_Vertical_Bar_Leaves_Out_AM_And_PM_But_A_Screen_Reader_Still_Hears_It()
    {
        Assert.Equal("2:05", ClockText.Time(Afternoon, true, false, true, English));
        Assert.Equal("2:05:09", ClockText.Time(Afternoon, true, true, true, English));
        Assert.Equal("2:05 PM", ClockText.Spoken(Afternoon, true, English));
        Assert.Equal("14:05", ClockText.Spoken(Afternoon, false, English));
    }

    [Fact]
    public void A_Culture_With_No_Designators_Still_Gets_AM_And_PM()
    {
        // Without the fallback a forced 12-hour clock here reads "2:05 " in the morning and the
        // afternoon alike.
        var bare = (CultureInfo)English.Clone();
        bare.DateTimeFormat.AMDesignator = string.Empty;
        bare.DateTimeFormat.PMDesignator = string.Empty;

        Assert.Equal("2:05 PM", ClockText.Time(Afternoon, true, false, false, bare));
        Assert.Equal("9:30 AM", ClockText.Time(Morning, true, false, false, bare));
    }

    /// <summary>
    /// The reserved width must hold every time the clock can show, or the row shifts sideways
    /// when the clock passes a wide one. Measured in the bar's own font at its own size, every
    /// minute of the day with seconds at their widest.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_Reserved_Width_Holds_Every_Time_Of_Day(bool twelveHour) => OnSta(() =>
    {
        double reserved = ClockText.WidestCandidates(twelveHour, English).Max(Width);

        for (int minute = 0; minute < 24 * 60; minute++)
        {
            foreach (int second in new[] { 0, 8, 59 })
            {
                DateTime time = new DateTime(2026, 1, 1).AddMinutes(minute).AddSeconds(second);
                string shown = ClockText.Time(time, twelveHour, true, false, English);
                Assert.True(Width(shown) <= reserved + 0.01, $"{shown} is wider than the {reserved:0.0} DIP reserved");
            }
        }
    });

    [Fact]
    public void The_Setting_Defaults_To_Windows_Round_Trips_And_Refuses_Junk()
    {
        Assert.Equal(ClockFormat.Windows, new UmbraSettings().ClockFormat);
        Assert.Equal(ClockFormat.TwelveHour, new UmbraSettings { ClockFormat = ClockFormat.TwelveHour }.Clone().ClockFormat);

        string path = Path.Combine(Path.GetTempPath(), $"umbra-clock-{Guid.NewGuid():N}.txt");
        try
        {
            new UmbraSettings { ClockFormat = ClockFormat.TwentyFourHour }.Save(path);
            Assert.Equal(ClockFormat.TwentyFourHour, UmbraSettings.Load(path).ClockFormat);

            File.WriteAllLines(path, ["clockFormat = twelvehour"]);
            Assert.Equal(ClockFormat.TwelveHour, UmbraSettings.Load(path).ClockFormat);

            // Read by name only, like every enum in the file: a number is never something Umbra wrote.
            File.WriteAllLines(path, ["clockFormat = 2"]);
            Assert.Equal(ClockFormat.Windows, UmbraSettings.Load(path).ClockFormat);

            File.WriteAllLines(path, ["clockFormat = 12h"]);
            Assert.Equal(ClockFormat.Windows, UmbraSettings.Load(path).ClockFormat);
        }
        finally { File.Delete(path); }
    }

    private static double Width(string text) => new FormattedText(
        text, English, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        14, Brushes.White, 1.0).WidthIncludingTrailingWhitespace;

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
