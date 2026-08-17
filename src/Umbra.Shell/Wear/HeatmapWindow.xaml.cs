using System.Windows;
using Umbra.Core.Wear;

namespace Umbra.Shell.Wear;

/// <summary>The phase-1 heatmap view (design-spec §9). Read-only: nothing here feeds back.</summary>
internal partial class HeatmapWindow : Window
{
    public HeatmapWindow(WearAccumulator accumulator)
    {
        InitializeComponent();

        Heatmap.Source = HeatmapRenderer.Render(accumulator);
        Summary.Text = HeatmapRenderer.Summarise(accumulator);
    }
}
