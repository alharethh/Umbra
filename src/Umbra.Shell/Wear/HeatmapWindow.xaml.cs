using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Umbra.Core.Wear;
using Umbra.Shell.Diagnostics;

namespace Umbra.Shell.Wear;

/// <summary>The phase-1 heatmap view (design-spec §9). Read-only: nothing here feeds back.</summary>
internal partial class HeatmapWindow : Window
{
    private readonly WearAccumulator _accumulator;
    private readonly Func<WearAccumulator?> _recording;

    /// <param name="accumulator">The dataset to show.</param>
    /// <param name="recording">The dataset the bar is recording into now, asked at each copy or save.</param>
    public HeatmapWindow(WearAccumulator accumulator, Func<WearAccumulator?> recording)
    {
        InitializeComponent();

        _accumulator = accumulator;
        _recording = recording;
        Heatmap.Source = HeatmapRenderer.Render(accumulator);
        Summary.Text = HeatmapRenderer.Summarise(accumulator);
    }

    // Composed at the click, not when the window opened: the accumulator is live, and a picture
    // saved an hour after opening the window should say so.
    private BitmapSource Picture() => HeatmapPicture.Compose(_accumulator, DateTime.Now);

    private bool Superseded => HeatmapPicture.IsSuperseded(_accumulator, _recording());

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        BitmapSource picture = Picture();

        // A bitmap for everything that pastes one, and PNG beside it, which is what browsers and
        // chat applications prefer and which keeps the pixels exact.
        DataObject data = new();
        data.SetImage(picture);

        MemoryStream png = new();
        HeatmapPicture.SavePng(picture, png);
        png.Position = 0;
        data.SetData("PNG", png, autoConvert: false);

        try
        {
            // Another process holding the clipboard open is ordinary; WPF's own retry gives up
            // after about a second and throws.
            Clipboard.SetDataObject(data, copy: true);
            Status.Text = HeatmapPicture.Outcome("Copied", Superseded);
            BringUpLog.Write($"heatmap copied: {picture.PixelWidth}x{picture.PixelHeight}");
        }
        catch (COMException ex)
        {
            Status.Text = "The clipboard is busy. Try again.";
            BringUpLog.Write($"heatmap copy failed: 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            FileName = HeatmapPicture.FileName(DateTime.Now),
            DefaultExt = ".png",
            Filter = "PNG image (*.png)|*.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };

        if (dialog.ShowDialog(this) != true) return;

        Save(dialog.FileName);
    }

    private void Save(string path)
    {
        BitmapSource picture = Picture();

        try
        {
            using FileStream file = File.Create(path);
            HeatmapPicture.SavePng(picture, file);
            Status.Text = HeatmapPicture.Outcome("Saved", Superseded);
            BringUpLog.Write($"heatmap saved: {picture.PixelWidth}x{picture.PixelHeight} -> {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "Could not save there.";
            BringUpLog.Write($"heatmap save failed: {path}: {ex.Message}");
        }
    }
}
