using Umbra.Core.Layout;
using Umbra.Core.Motion;

namespace Umbra.Core.Tests;

/// <summary>
/// Design-spec §6.2 and §6.3 — the derived drift amplitudes, and P5's slot containment.
/// </summary>
public class LayoutTests
{
    private const double ScreenWidth = 3840.0;
    private const double ScreenHeight = 2160.0;

    // The §6.2 worked table: scaling, pitch, icon, gutter, A_max, drift, micro, peak, clearance.
    public static TheoryData<double, double, double, double, double, double, double, double, double> ScalingTable => new()
    {
        { 1.00, 44, 24, 10.0, 7.80, 5.46, 1.95, 7.41, 2.59 },
        { 1.25, 55, 30, 12.5, 9.75, 6.83, 2.44, 9.26, 3.24 },
        { 1.50, 66, 36, 15.0, 11.70, 8.19, 2.93, 11.12, 3.89 },
        { 1.75, 77, 42, 17.5, 13.65, 9.56, 3.41, 12.97, 4.53 },
        { 2.00, 88, 48, 20.0, 15.60, 10.92, 3.90, 14.82, 5.18 },
    };

    [Theory]
    [MemberData(nameof(ScalingTable))]
    public void Amplitudes_Are_Derived_From_Layout_At_Every_Dpi(
        double scale, double pitch, double icon, double gutter,
        double maxAmplitude, double drift, double micro, double peak, double clearance)
    {
        SlotMetrics slot = SlotMetrics.ForScale(scale);

        Assert.Equal(pitch, slot.Pitch);
        Assert.Equal(icon, slot.IconExtent);
        Approx.Equal(gutter, slot.Gutter, 1e-9, "gutter");
        // The published table rounds to 1–2 dp; allow one unit in the last place.
        Approx.Equal(maxAmplitude, slot.MaxAmplitude, 0.006, "A_max");
        Approx.Equal(drift, slot.DriftAmplitude, 0.006, "drift amplitude");
        Approx.Equal(micro, slot.MicroAmplitude, 0.006, "micro amplitude");
        Approx.Equal(peak, slot.PeakOffset, 0.006, "peak total offset");
        Approx.Equal(clearance, slot.Clearance, 0.006, "clearance to the neighbouring slot");
    }

    [Fact]
    public void Verified_Numbers_At_100_Percent()
    {
        SlotMetrics slot = SlotMetrics.ForScale(1.0);

        Approx.Equal(7.80, slot.MaxAmplitude, 1e-9, "A_max @100% (pitch 44, icon 24)");
        Approx.Equal(5.46, slot.DriftAmplitude, 1e-9, "drift amplitude (0.70·A_max)");
        Approx.Equal(1.95, slot.MicroAmplitude, 1e-9, "micro amplitude (0.25·A_max)");
        Approx.Equal(7.41, slot.PeakOffset, 1e-9, "peak offset");
        Approx.Equal(2.59, slot.Clearance, 1e-9, "clearance");
    }

    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(1.75)]
    [InlineData(2.00)]
    public void P5_Holds_At_Every_Dpi(double scale)
    {
        SlotMetrics slot = SlotMetrics.ForScale(scale);

        Assert.True(slot.SatisfiesSlotContainment);
        Assert.True(slot.Clearance > 0.0);
        Approx.Relative(0.95, slot.PeakOffset / slot.MaxAmplitude, 1e-12,
            "drift and micro are additive: 0.70 + 0.25 of A_max");
    }

    [Theory]
    [InlineData(BarEdge.Bottom)]
    [InlineData(BarEdge.Top)]
    [InlineData(BarEdge.Left)]
    [InlineData(BarEdge.Right)]
    public void Drawn_Content_Never_Leaves_Its_Own_Hit_Slot(BarEdge edge)
    {
        // P5, checked across a full drift cycle rather than at its extremes only: hit geometry is
        // fixed and computed from the undrifted layout, and drawn geometry moves only inside it.
        BarMetrics bar = new(edge, Alignment.Start);
        const int slots = 10;

        for (double t = 0.0; t < MotionModel.DriftPeriodSeconds; t += 5.0)
        {
            MotionOffset offset = MotionModel.IconRow(t, bar.Slot, bar.ShortAxisDriftAmplitude);

            for (int i = 0; i < slots; i++)
            {
                Rect hit = bar.HitSlot(i, slots, ScreenWidth, ScreenHeight);
                Rect drawn = bar.DrawnIcon(i, slots, ScreenWidth, ScreenHeight, offset.Along, offset.Across);

                Assert.True(hit.Contains(drawn),
                    $"{edge} slot {i} at t={t:F0}s: drawn {drawn} escaped hit rect {hit}.");
            }
        }
    }

    [Fact]
    public void Drawn_Content_Never_Reaches_A_Neighbours_Hit_Slot()
    {
        // v1's ±64 px put icons on top of their neighbours' click targets. This is the property
        // that replaced it — and the reason drift is ±5.46 px rather than ±64.
        BarMetrics bar = new(BarEdge.Bottom, Alignment.Start);
        const int slots = 8;

        for (double t = 0.0; t < MotionModel.DriftPeriodSeconds; t += 5.0)
        {
            MotionOffset offset = MotionModel.IconRow(t, bar.Slot, bar.ShortAxisDriftAmplitude);

            for (int i = 0; i < slots; i++)
            {
                Rect drawn = bar.DrawnIcon(i, slots, ScreenWidth, ScreenHeight, offset.Along, offset.Across);

                for (int j = 0; j < slots; j++)
                {
                    if (i == j) continue;
                    Rect neighbour = bar.HitSlot(j, slots, ScreenWidth, ScreenHeight);
                    Assert.False(drawn.Intersects(neighbour),
                        $"Slot {i}'s icon overlapped slot {j}'s hit rect at t={t:F0}s.");
                }
            }
        }
    }

    [Fact]
    public void V1_Amplitude_Would_Have_Violated_P5()
    {
        // §6.0: meaningful interior wear reduction needs amplitude greater than the icon's own
        // width, which is nearly three times the whole gutter. v1 achieved it and paid for it in
        // click targets; v2 declines to pay that price.
        SlotMetrics slot = SlotMetrics.ForScale(1.0);
        const double v1Amplitude = 64.0;

        Assert.True(v1Amplitude > slot.Gutter + slot.IconExtent / 2.0,
            "v1's drift put icons clean over their neighbours.");
        Assert.True(slot.PeakOffset < slot.IconExtent,
            "Slot-bounded drift cannot exceed the icon width, so it cannot protect the interior.");
    }

    [Fact]
    public void Row_Is_Inset_Far_Enough_That_Drift_Cannot_Clip_At_The_Corner()
    {
        // §6.3: with a left-aligned row the first slot sits at the start of the bar, so there is
        // nowhere for negative drift to go. Take the larger of Windows 11's own padding and the
        // peak offset.
        BarMetrics measured = new(BarEdge.Bottom, Alignment.Start, 1.0);
        Approx.Equal(8.0, measured.RowInset, 1e-9, "row inset at the Windows 11 padding");

        // If the measurement comes back smaller than the peak offset, the peak offset wins.
        BarMetrics unmeasured = new(BarEdge.Bottom, Alignment.Start, 1.0, measuredWindows11RowInset: 0.0);
        Approx.Equal(7.41, unmeasured.RowInset, 1e-9, "row inset falls back to the peak offset");

        for (double t = 0.0; t < MotionModel.DriftPeriodSeconds; t += 5.0)
        {
            MotionOffset offset = MotionModel.IconRow(t, unmeasured.Slot, unmeasured.ShortAxisDriftAmplitude);
            Rect first = unmeasured.DrawnIcon(0, 8, ScreenWidth, ScreenHeight, offset.Along, offset.Across);

            Assert.True(first.Left >= 0.0, $"First icon clipped at the screen corner at t={t:F0}s ({first.Left:F2}).");
        }
    }

    [Fact]
    public void Reserved_Rect_Matches_The_Published_Metrics()
    {
        BarMetrics horizontal = new(BarEdge.Bottom);
        BarMetrics vertical = new(BarEdge.Left);

        Assert.Equal(48.0, horizontal.ContentBand);
        Assert.Equal(48.0, horizontal.ReservedExtent);
        Assert.Equal(56.0, vertical.ContentBand);
        Assert.Equal(56.0, vertical.ReservedExtent);

        // §8 credited an extra 8 px to short-axis drift and corner rounding. Neither needs it, so
        // the reserved band is the content band — which also makes the bar exactly as tall as the
        // Windows 11 taskbar it replaces.
        Assert.Equal(0.0, horizontal.ReservedExtent - horizontal.ContentBand);
        Assert.Equal(0.0, vertical.ReservedExtent - vertical.ContentBand);

        Rect bottom = horizontal.ReservedRect(ScreenWidth, ScreenHeight);
        Assert.Equal(new Rect(0, ScreenHeight - 48, ScreenWidth, 48), bottom);
    }

    [Fact]
    public void Long_Axis_Needs_No_Drift_Headroom()
    {
        // v1 reserved ≥64 px per side along the bar's length. §6.3 deletes it: drift is contained
        // within slots, so the reserved rectangle spans the full screen edge with nothing held back.
        BarMetrics bar = new(BarEdge.Bottom);
        Rect reserved = bar.ReservedRect(ScreenWidth, ScreenHeight);

        Assert.Equal(0.0, reserved.Left);
        Assert.Equal(ScreenWidth, reserved.Right);
    }

    [Fact]
    public void Short_Axis_Drift_Is_Contained_By_The_Content_Band_Alone()
    {
        // The resolution of §8's reserved-band discrepancy. §6.2's formula gives 4.8 px of drift
        // and micro-jitter adds 1.95 px on the same axis, for 6.75 px of travel — which §8's 8 px
        // (4 per side) never covered. It did not need to: the content band already leaves 12 px of
        // clearance either side of a 24 px icon, so the drift is contained without any reserved
        // margin at all, and the bar can be exactly as tall as the taskbar it replaces.
        BarMetrics bar = new(BarEdge.Bottom);

        Approx.Equal(9.6, bar.ShortAxisMaxAmplitude, 1e-9, "A_max_short");
        Approx.Equal(4.8, bar.ShortAxisDriftAmplitude, 1e-9, "0.50 × A_max_short");
        Approx.Equal(6.75, bar.ShortAxisPeakOffset, 1e-9, "short-axis drift plus micro-jitter");

        Approx.Equal(12.0, (bar.ContentBand - bar.Slot.IconExtent) / 2.0, 1e-9, "clearance within the band");
        Assert.True(bar.ShortAxisFitsContentBand);
        Assert.Equal(0.0, bar.ReservedHeadroom);
    }

    [Theory]
    [InlineData(BarEdge.Bottom)]
    [InlineData(BarEdge.Top)]
    [InlineData(BarEdge.Left)]
    [InlineData(BarEdge.Right)]
    public void Short_Axis_Travel_Fits_Every_Edge_Without_Reserved_Headroom(BarEdge edge)
    {
        BarMetrics bar = new(edge);

        Assert.True(bar.ShortAxisFitsContentBand,
            $"{edge}: {bar.ShortAxisPeakOffset:F2} px of travel against " +
            $"{(bar.ContentBand - bar.Slot.IconExtent) / 2.0:F2} px of clearance.");
    }

    [Fact]
    public void Start_Button_Claims_The_Screen_Corner_On_A_Horizontal_Bar()
    {
        // §6.5, and a rare case of being strictly better than the reference: Windows 11 insets its
        // own Start button and loses the corner as a Fitts target. We draw at the Win11 inset for
        // fidelity and extend the hit rect to the corner, which costs nothing because hit geometry
        // is invisible.
        BarMetrics bar = new(BarEdge.Bottom, Alignment.Start);

        Rect start = bar.StartHitSlot(8, ScreenWidth, ScreenHeight);
        Rect drawn = bar.DrawnIcon(0, 8, ScreenWidth, ScreenHeight);

        Assert.Equal(0.0, start.Left);
        Assert.Equal(ScreenHeight, start.Bottom);
        Assert.True(start.Contains(drawn), "The glyph still sits at the Windows 11 inset, inside the extended rect.");
        Assert.True(drawn.Left > 0.0, "Only the hit geometry reaches the corner — the glyph does not.");
    }

    [Fact]
    public void Start_Button_Sits_At_The_Bottom_Of_A_Vertical_Bar()
    {
        // §7.3: the Start menu opens at the bottom of the screen regardless of our edge, so a
        // bottom-anchored Start button sits adjacent to it rather than diagonally opposite.
        BarMetrics bar = new(BarEdge.Left, Alignment.Start);
        const int slots = 8;

        Rect start = bar.HitSlot(0, slots, ScreenWidth, ScreenHeight);
        Rect second = bar.HitSlot(1, slots, ScreenWidth, ScreenHeight);

        Assert.True(start.Top > second.Top, "Slot 0 is the bottom-most slot on a vertical bar.");
        Approx.Equal(ScreenHeight - bar.RowInset, start.Bottom, 1e-9, "row is inset from the bottom end");

        Rect corner = bar.StartHitSlot(slots, ScreenWidth, ScreenHeight);
        Assert.Equal(0.0, corner.Left);
        Assert.Equal(ScreenHeight, corner.Bottom);
    }

    [Fact]
    public void Centred_Alignment_Claims_No_Corner()
    {
        BarMetrics bar = new(BarEdge.Bottom, Alignment.Center);
        const int slots = 8;

        Rect start = bar.StartHitSlot(slots, ScreenWidth, ScreenHeight);

        Assert.Equal(bar.HitSlot(0, slots, ScreenWidth, ScreenHeight), start);
        Assert.True(start.Left > 0.0, "A centred row has no corner to claim.");
    }

    [Fact]
    public void The_Tray_Drifts_Less_Because_Its_Slots_Are_Smaller()
    {
        // P5 applies per slot, and a tray slot is not a task button. Applying the icon row's
        // amplitude to a 4 px gutter would put tray icons outside their own slots.
        BarMetrics bar = new(BarEdge.Bottom);

        Assert.Equal(24.0, bar.TraySlot.Pitch);
        Assert.Equal(16.0, bar.TraySlot.IconExtent);
        Approx.Equal(4.0, bar.TraySlot.Gutter, 1e-9, "tray gutter");

        Assert.True(bar.Slot.PeakOffset > bar.TraySlot.Gutter,
            "Precondition: the icon row's offset would not fit a tray slot.");

        Assert.True(bar.TraySlot.SatisfiesSlotContainment);
        Assert.True(bar.TraySlot.PeakOffset < bar.TraySlot.Gutter);
        Assert.True(bar.TrayDriftRatio is > 0.0 and < 1.0);
    }

    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(2.00)]
    public void Tray_Slots_Contain_Their_Icons_At_Every_Dpi(double scale)
    {
        BarMetrics bar = new(BarEdge.Bottom, Alignment.Start, scale);

        Assert.True(bar.TraySlot.SatisfiesSlotContainment);
        Assert.True(bar.TraySlot.Clearance > 0.0);
    }

    [Fact]
    public void Bottom_Is_The_Only_Edge_That_Contends_With_Explorer()
    {
        // §11.1: Explorer's taskbar is autohidden on its own edge, and pointer travel there
        // triggers its reveal. Bottom is the shipping default, so this is a release blocker rather
        // than something to route around — but the left edge is where the bar comes up first.
        Assert.True(BarEdge.Bottom.ContendsWithExplorerTaskbar());
        Assert.False(BarEdge.Left.ContendsWithExplorerTaskbar());
        Assert.False(BarEdge.Top.ContendsWithExplorerTaskbar());
        Assert.False(BarEdge.Right.ContendsWithExplorerTaskbar());
    }
}
