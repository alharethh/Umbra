using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Core.Wear;
using Umbra.Shell.Wear;

namespace Umbra.Shell.Tests;

/// <summary>
/// The picture the heatmap window saves and copies: the README's layout, rendered from the data,
/// with the hours and the name in a caption strip.
/// </summary>
public class HeatmapPictureTests
{
    private static readonly DateTime Day = new(2026, 10, 23, 14, 5, 0);

    [Fact]
    public void The_File_Name_Carries_The_Date()
    {
        Assert.Equal("umbra-heatmap-2026-10-23.png", HeatmapPicture.FileName(Day));
    }

    [Fact]
    public void A_Window_Left_Open_Across_A_Move_Knows_Its_Data_Is_The_Old_Grid()
    {
        // What BarWindow.StartWear does on an edge change: a new accumulator at the new size,
        // while the window goes on holding the one it was opened with.
        WearAccumulator opened = new(2560, 60);
        WearAccumulator afterMove = new(70, 1440);

        Assert.False(HeatmapPicture.IsSuperseded(opened, opened));
        Assert.True(HeatmapPicture.IsSuperseded(opened, afterMove));

        // Same size is still a different dataset: a move to another monitor of the same width.
        Assert.True(HeatmapPicture.IsSuperseded(opened, new WearAccumulator(2560, 60)));

        // No sampler (the bar is closing) is nothing to compare against, not a move.
        Assert.False(HeatmapPicture.IsSuperseded(opened, null));
    }

    [Fact]
    public void The_Status_Says_When_The_Picture_Is_From_Before_The_Move()
    {
        Assert.Equal("Saved", HeatmapPicture.Outcome("Saved", superseded: false));
        Assert.Equal("Copied: the record from before the bar moved",
            HeatmapPicture.Outcome("Copied", superseded: true));
    }

    [Theory]
    [InlineData(0.0, "0.0 hours")]
    [InlineData(1440.0, "0.4 hours")]
    [InlineData(3600.0, "1.0 hour")]
    [InlineData(9.94 * 3600, "9.9 hours")]
    [InlineData(312.6 * 3600, "313 hours")]
    [InlineData(1234.0 * 3600, "1,234 hours")]
    public void The_Caption_States_The_Hours_The_Data_Covers(double seconds, string expected)
    {
        string caption = HeatmapPicture.Caption(seconds, Day);

        Assert.Contains($"{expected} recorded", caption);
        Assert.Contains("2026-10-23", caption);
    }

    [Fact]
    public void A_Horizontal_Bar_Composes_At_The_Readme_Size()
    {
        WearAccumulator wear = Sample(2560, 60);

        BitmapSource picture = OnSta(() => HeatmapPicture.Compose(wear, Day));

        // 2560 x 60 -> whole strip at 1280 x 30, gap, leading 640 px at 2x (120 tall), caption.
        Assert.Equal(HeatmapPicture.Width, picture.PixelWidth);
        Assert.Equal(30 + HeatmapPicture.Gap + 120 + HeatmapPicture.CaptionHeight, picture.PixelHeight);
    }

    [Fact]
    public void A_Vertical_Bar_Is_Turned_To_Read_Along_The_Picture()
    {
        // 70 x 1440, as the left edge measured at 125%. Turned, it is a 1440 x 70 strip.
        WearAccumulator wear = Sample(70, 1440);

        BitmapSource picture = OnSta(() => HeatmapPicture.Compose(wear, Day));

        int whole = (int)Math.Round(70 * (1280 / 1440.0));
        Assert.Equal(HeatmapPicture.Width, picture.PixelWidth);
        Assert.Equal(whole + HeatmapPicture.Gap + 140 + HeatmapPicture.CaptionHeight, picture.PixelHeight);
    }

    [Fact]
    public void The_Peak_Cell_Is_Drawn_Where_The_Data_Puts_It_At_Exact_Pixels()
    {
        // One hot cell at the leading corner. In the magnified section it must be a crisp 2 x 2
        // block at the ramp's top colour, and its neighbour black: smoothing would smear it.
        WearAccumulator wear = new(2560, 60);
        wear.Add(0, 0, 1.0, 3600);

        byte[] px = OnSta(() => Pixels(HeatmapPicture.Compose(wear, Day)));
        int detailTop = 30 + HeatmapPicture.Gap;

        (byte r, byte g, byte b) = HeatmapPixels.Ramp(1.0);
        Assert.Equal((r, g, b), At(px, 0, detailTop));
        Assert.Equal((r, g, b), At(px, 1, detailTop + 1));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(px, 2, detailTop));
    }

    [Fact]
    public void A_Vertical_Bars_Top_Is_The_Magnified_Leading_End()
    {
        // The top of a left-edge bar, where Start is, turned to the left of the picture: the
        // magnified section's first column, at the edge of the strip nearest the screen's edge.
        WearAccumulator wear = new(70, 1440);
        wear.Add(0, 0, 1.0, 3600);

        byte[] px = OnSta(() => Pixels(HeatmapPicture.Compose(wear, Day)));
        int whole = (int)Math.Round(70 * (1280 / 1440.0));
        int bottom = whole + HeatmapPicture.Gap + 140 - 1;

        (byte r, byte g, byte b) = HeatmapPixels.Ramp(1.0);
        Assert.Equal((r, g, b), At(px, 0, bottom));
        Assert.Equal((r, g, b), At(px, 1, bottom - 1));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(px, 2, bottom));
    }

    [Fact]
    public void The_Caption_And_The_Name_Are_Actually_Drawn()
    {
        WearAccumulator wear = Sample(2560, 60);

        byte[] px = OnSta(() => Pixels(HeatmapPicture.Compose(wear, Day)));
        int top = 30 + HeatmapPicture.Gap + 120;

        // The caption on the left in grey, the name in the right-hand corner in the logo's cyan,
        // and nothing in between.
        Assert.True(LitIn(px, 0, top, 400, HeatmapPicture.CaptionHeight, cyan: false) > 50);
        Assert.True(LitIn(px, HeatmapPicture.Width - 90, top, 90, HeatmapPicture.CaptionHeight, cyan: true) > 20);
        Assert.Equal(0, LitIn(px, 600, top, 300, HeatmapPicture.CaptionHeight, cyan: false));
    }

    [Fact]
    public void The_Png_Round_Trips()
    {
        WearAccumulator wear = Sample(2560, 60);

        (int w, int h) = OnSta(() =>
        {
            BitmapSource picture = HeatmapPicture.Compose(wear, Day);
            using MemoryStream png = new();
            HeatmapPicture.SavePng(picture, png);
            png.Position = 0;
            BitmapFrame read = new PngBitmapDecoder(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            return (read.PixelWidth, read.PixelHeight);
        });

        Assert.Equal((1280, 204), (w, h));
    }

    private static WearAccumulator Sample(int width, int height)
    {
        WearAccumulator wear = new(width, height);
        for (int x = 4; x < Math.Min(width, 40); x++)
            for (int y = 4; y < Math.Min(height, 40); y++)
                wear.Add(x, y, 0.01 * (x + y), 1);
        wear.Idle(3 * 3600);
        return wear;
    }

    private static byte[] Pixels(BitmapSource picture)
    {
        FormatConvertedBitmap bgra = new(picture, PixelFormats.Bgra32, null, 0);
        byte[] px = new byte[picture.PixelWidth * picture.PixelHeight * 4];
        bgra.CopyPixels(px, picture.PixelWidth * 4, 0);
        return px;
    }

    private static (byte, byte, byte) At(byte[] px, int x, int y)
    {
        int p = (y * HeatmapPicture.Width + x) * 4;
        return (px[p + 2], px[p + 1], px[p]);
    }

    private static int LitIn(byte[] px, int x0, int y0, int w, int h, bool cyan)
    {
        int count = 0;
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                (byte r, byte g, byte b) = At(px, x, y);
                if (r + g + b < 120) continue;
                if (!cyan || b > r + 30) count++;
            }
        }
        return count;
    }

    // WPF's visuals and RenderTargetBitmap want an STA thread; xUnit's are MTA.
    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("STA work failed", failure);
        return result;
    }
}
