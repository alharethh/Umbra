using System.IO;
using Umbra.Core.Layout;

namespace Umbra.Shell.Tests;

/// <summary>
/// The overflow flyout's cells are the same shape on every edge (backlog 3b).
/// </summary>
/// <remarks>
/// The flyout is a grid laid out left to right wherever the bar sits. It used to size each cell
/// from the bar's own tray slot, which on a vertical bar is the bar's thickness wide — QA measured
/// 84×39 px cells and a 357×60 panel on the left edge against 177×93 on the bottom.
/// </remarks>
public class TrayOverflowCellTests
{
    private static readonly BarEdge[] Edges = [BarEdge.Bottom, BarEdge.Top, BarEdge.Left, BarEdge.Right];

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Every_Edge_Gets_The_Bottom_Edges_Cell(double scale)
    {
        var bottom = BarWindow.TrayOverflowCellFor(new BarMetrics(BarEdge.Bottom, scale: scale));

        foreach (BarEdge edge in Edges)
        {
            var cell = BarWindow.TrayOverflowCellFor(new BarMetrics(edge, scale: scale));
            Assert.Equal(bottom, cell);
        }
    }

    /// <summary>A cell is a tray pitch wide, not a bar thickness.</summary>
    [Fact]
    public void A_Cell_Is_One_Tray_Pitch_Wide()
    {
        var cell = BarWindow.TrayOverflowCellFor(new BarMetrics(BarEdge.Left, scale: 1.5));

        Assert.Equal(BarWindow.TrayPitchExtent * 1.5, cell.Width, 9);
        Assert.True(cell.Width < BarMetrics.BaseVerticalContentBand * 1.5);
    }

    /// <summary>
    /// The flyout's icons drift with <c>TrayOffset</c>, and on a vertical bar that puts the
    /// short-axis travel on the cell's narrow axis. It must still fit inside the cell (P5).
    /// </summary>
    [Theory]
    [InlineData(BarEdge.Left, 1.0)]
    [InlineData(BarEdge.Right, 1.5)]
    [InlineData(BarEdge.Left, 1.5)]
    public void Vertical_Short_Axis_Drift_Stays_Inside_A_Flyout_Cell(BarEdge edge, double scale)
    {
        foreach (double dormant in new[] { 12.0, 16.0, 20.0, 24.0, 26.0 })
        {
            var metrics = new BarMetrics(edge, scale: scale, baseIconExtent: dormant);
            var cell = BarWindow.TrayOverflowCellFor(metrics);

            double peak = metrics.ShortAxisPeakOffset * BarWindow.TrayDriftRatioFor(metrics);
            double room = (cell.Width - BarWindow.TrayGlyphExtent * scale) / 2.0;

            Assert.True(peak < room, $"{edge} at {scale}x, {dormant} px icon: {peak:F2} px of travel in {room:F2} px");
        }
    }

    /// <summary>The flyout's template is what uses the cell, and only the flyout uses it.</summary>
    [Fact]
    public void The_Flyout_Template_Is_Sized_By_The_Cell()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Umbra.Shell", "BarWindow.xaml"));

        int start = xaml.IndexOf("<DataTemplate x:Key=\"TrayIconTemplate\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "TrayIconTemplate not found");
        string template = xaml[start..xaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal)];

        Assert.Contains("TrayOverflowCellWidth", template);
        Assert.Contains("TrayOverflowCellHeight", template);
        Assert.DoesNotContain("TraySlotWidth", template);
        Assert.DoesNotContain("TraySlotHeight", template);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Umbra.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Umbra.slnx not found above " + AppContext.BaseDirectory);
    }
}
