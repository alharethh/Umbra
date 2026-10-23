using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

public class WheelVolumeTests
{
    private sealed class FakeVolume(float level, bool muted = false) : IMasterVolume
    {
        public float Level { get; set; } = level;
        public bool Muted { get; set; } = muted;
        public int Writes { get; private set; }

        float IMasterVolume.Level
        {
            get => Level;
            set { Level = value; Writes++; }
        }
    }

    [Fact]
    public void A_Notch_Up_Is_Two_Percent()
    {
        var v = new FakeVolume(0.40f);
        var result = new WheelVolume(v).Apply(120);

        Assert.Equal((42, false), result);
        Assert.Equal(0.42f, v.Level, 4);
    }

    [Fact]
    public void A_Notch_Down_Is_Two_Percent()
    {
        var v = new FakeVolume(0.40f);
        Assert.Equal((38, false), new WheelVolume(v).Apply(-120));
    }

    [Fact]
    public void Steps_From_The_Whole_Percentage_Windows_Shows()
    {
        // 33.4% reads as 33%; the step lands on 35, not 35.4.
        var v = new FakeVolume(0.334f);
        Assert.Equal((35, false), new WheelVolume(v).Apply(120));
        Assert.Equal(0.35f, v.Level, 4);
    }

    [Theory]
    [InlineData(0.99f, 120, 100)]
    [InlineData(1.00f, 360, 100)]
    [InlineData(0.01f, -120, 0)]
    [InlineData(0.00f, -240, 0)]
    public void Clamps_At_Both_Ends(float start, int delta, int expected)
    {
        var v = new FakeVolume(start);
        Assert.Equal(expected, new WheelVolume(v).Apply(delta)!.Value.Percent);
        Assert.InRange(v.Level, 0f, 1f);
    }

    [Fact]
    public void Touchpad_Deltas_Accumulate_To_One_Step_Per_Notch()
    {
        var v = new FakeVolume(0.50f);
        var wheel = new WheelVolume(v);

        // A precision touchpad sends small deltas; 12 × 10 = one notch, not twelve steps.
        for (int i = 0; i < 11; i++) Assert.Null(wheel.Apply(10));
        Assert.Equal((52, false), wheel.Apply(10));
        Assert.Equal(0.52f, v.Level, 4);
    }

    [Fact]
    public void Several_Notches_In_One_Event_Step_Several_Times()
    {
        var v = new FakeVolume(0.50f);
        Assert.Equal((56, false), new WheelVolume(v).Apply(360));
    }

    [Fact]
    public void A_Reversed_Partial_Notch_Cancels_Rather_Than_Stepping()
    {
        var v = new FakeVolume(0.50f);
        var wheel = new WheelVolume(v);

        Assert.Null(wheel.Apply(60));
        Assert.Null(wheel.Apply(-60));
        Assert.Equal(0, v.Writes);
    }

    [Fact]
    public void Reset_Drops_A_Part_Collected_Notch()
    {
        var v = new FakeVolume(0.50f);
        var wheel = new WheelVolume(v);

        Assert.Null(wheel.Apply(100));
        wheel.Reset();
        Assert.Null(wheel.Apply(100));
        Assert.Equal(0, v.Writes);
    }

    [Fact]
    public void Up_Unmutes()
    {
        var v = new FakeVolume(0.30f, muted: true);
        Assert.Equal((32, false), new WheelVolume(v).Apply(120));
        Assert.False(v.Muted);
    }

    [Fact]
    public void Down_Leaves_The_Mute_Alone()
    {
        var v = new FakeVolume(0.30f, muted: true);
        Assert.Equal((28, true), new WheelVolume(v).Apply(-120));
        Assert.True(v.Muted);
    }

    [Fact]
    public void Up_At_Full_Volume_Still_Unmutes()
    {
        var v = new FakeVolume(1.0f, muted: true);
        Assert.Equal((100, false), new WheelVolume(v).Apply(120));
    }

    [Fact]
    public void An_Unreadable_Level_Changes_Nothing()
    {
        var v = new FakeVolume(float.NaN);
        Assert.Null(new WheelVolume(v).Apply(-120));
        Assert.Equal(0, v.Writes);
    }

    [Fact]
    public void Describes_Level_And_Mute()
    {
        Assert.Equal("Volume: 34%", WheelVolume.Describe(34, false));
        Assert.Equal("Volume: 34% (muted)", WheelVolume.Describe(34, true));
    }
}
