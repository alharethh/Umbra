using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// The saved auto-hide bit in Explorer's undocumented <c>StuckRects3</c> blob.
/// </summary>
/// <remarks>
/// The blob below is the one on the second machine, read on 2026-10-16 with auto-hide on (byte 8
/// <c>0x03</c>, left there by QA case 0.6). Nothing here writes to the registry.
/// </remarks>
public class StuckRectsTests
{
    private static byte[] RealBlob() =>
    [
        0x30, 0x00, 0x00, 0x00, 0xFE, 0xFF, 0xFF, 0xFF, 0x03, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
        0x48, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x98, 0x05, 0x00, 0x00,
        0xD0, 0x08, 0x00, 0x00, 0xE0, 0x05, 0x00, 0x00, 0x90, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
    ];

    [Fact]
    public void Reads_The_Real_Blob_As_Auto_Hide_On()
    {
        Assert.True(StuckRects.IsRecognised(RealBlob()));
        Assert.True(StuckRects.ReadAutoHide(RealBlob()));
    }

    [Fact]
    public void Turning_Auto_Hide_Off_Changes_Only_Bit_Zero_Of_Byte_Eight()
    {
        byte[] original = RealBlob();
        byte[] off = StuckRects.WithAutoHide(original, autoHide: false)!;

        Assert.Equal(0x02, off[8]);
        Assert.False(StuckRects.ReadAutoHide(off));

        for (int i = 0; i < original.Length; i++)
            if (i != 8) Assert.Equal(original[i], off[i]);

        // The input is untouched, so a failed write leaves nothing half-changed in memory.
        Assert.Equal(0x03, original[8]);
    }

    [Fact]
    public void Round_Trips()
    {
        byte[] off = StuckRects.WithAutoHide(RealBlob(), autoHide: false)!;
        byte[] on = StuckRects.WithAutoHide(off, autoHide: true)!;

        Assert.Equal(RealBlob(), on);
    }

    [Fact]
    public void Other_Flags_In_Byte_Eight_Survive()
    {
        byte[] blob = RealBlob();
        blob[8] = 0x0B;

        Assert.Equal(0x0A, StuckRects.WithAutoHide(blob, autoHide: false)![8]);
    }

    [Theory]
    [InlineData(47)]
    [InlineData(49)]
    [InlineData(0)]
    public void A_Blob_Of_Another_Length_Is_Refused(int length)
    {
        byte[] blob = new byte[length];
        Array.Copy(RealBlob(), blob, Math.Min(length, 48));

        Assert.Null(StuckRects.ReadAutoHide(blob));
        Assert.Null(StuckRects.WithAutoHide(blob, false));
    }

    [Theory]
    [InlineData(0, 0x31)]   // size field
    [InlineData(4, 0xFD)]   // version field
    public void A_Blob_With_Another_Header_Is_Refused(int offset, byte value)
    {
        byte[] blob = RealBlob();
        blob[offset] = value;

        Assert.Null(StuckRects.ReadAutoHide(blob));
        Assert.Null(StuckRects.WithAutoHide(blob, false));
    }

    [Fact]
    public void A_Missing_Value_Is_Refused()
    {
        Assert.Null(StuckRects.ReadAutoHide(null));
        Assert.Null(StuckRects.WithAutoHide(null, false));
    }
}
