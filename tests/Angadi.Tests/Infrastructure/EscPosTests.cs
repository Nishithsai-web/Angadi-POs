using Angadi.Application.Printing;
using Angadi.Infrastructure.Printing;
using SkiaSharp;

namespace Angadi.Tests.Infrastructure;

/// <summary>v2 printing: the exact ESC/POS bytes and the 1-bit raster conversion (no printer needed).</summary>
public sealed class EscPosTests
{
    [Fact]
    public void Commands_AreTheExactEscPosBytes()
    {
        Assert.Equal(new byte[] { 0x1B, 0x40 }, EscPos.Initialize());                                   // ESC @
        Assert.Equal(new byte[] { 0x1B, 0x64, 0x04, 0x1D, 0x56, 0x42, 0x00 }, EscPos.FeedAndCut());    // ESC d 4, GS V 66 0
        Assert.Equal(new byte[] { 0x10, 0x04, 0x04 }, EscPos.PaperStatusRequest());                     // DLE EOT 4
        Assert.Equal(384, EscPos.DotsFor(PaperWidth.Mm58));
        Assert.Equal(576, EscPos.DotsFor(PaperWidth.Mm80));
    }

    [Fact]
    public void MonoBitmap_PacksTheLeftmostDotIntoTheHighBit()
    {
        var image = new MonoBitmap(10, 2);
        Assert.Equal(2, image.BytesPerRow);
        image[0, 0] = true;
        image[9, 0] = true;
        image[7, 1] = true;
        image[8, 1] = true;
        Assert.Equal(new byte[] { 0x80, 0x40, 0x01, 0x80 }, image.Bits);
        image[0, 0] = false;
        Assert.Equal(0x00, image.Bits[0]);
        Assert.True(image[9, 0]);
    }

    [Fact]
    public void RasterBand_IsGsV0WithLittleEndianSizes()
    {
        var image = new MonoBitmap(16, 3);
        image[0, 1] = true;
        image[15, 2] = true;

        var band = EscPos.RasterBand(image, 1, 2);

        Assert.Equal(new byte[] { 0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00, 0x80, 0x00, 0x00, 0x01 }, band);
    }

    [Fact]
    public void Job_IsResetThenBandsThenFeedAndCut()
    {
        var image = new MonoBitmap(8, 3);
        image[0, 0] = true;
        image[1, 1] = true;
        image[2, 2] = true;

        var job = EscPos.Job(image, bandRows: 2);

        Assert.Equal(new byte[]
        {
            0x1B, 0x40,
            0x1D, 0x76, 0x30, 0x00, 0x01, 0x00, 0x02, 0x00, 0x80, 0x40,
            0x1D, 0x76, 0x30, 0x00, 0x01, 0x00, 0x01, 0x00, 0x20,
            0x1B, 0x64, 0x04, 0x1D, 0x56, 0x42, 0x00,
        }, job);
    }

    [Fact]
    public void Job_At80mm_Uses72BytesPerRow_AndBandsOf96Rows()
    {
        var job = EscPos.Job(new MonoBitmap(576, 200));
        // 2 (reset) + 3 bands (96, 96, 8 rows) × (8 header + 72 × rows) + 7 (feed/cut)
        Assert.Equal(2 + 3 * 8 + 72 * 200 + 7, job.Length);
        Assert.Equal(new byte[] { 0x1D, 0x76, 0x30, 0x00, 72, 0, 96, 0 }, job[2..10]);
    }

    [Theory]
    [InlineData(0x12, false)]   // paper present
    [InlineData(0x1E, false)]   // paper NEAR end: still prints
    [InlineData(0x72, true)]    // paper end
    [InlineData(0x7E, true)]    // near end + end
    [InlineData(0x60, false)]   // fixed bits wrong: not a DLE EOT 4 answer
    [InlineData(0xFF, false)]
    [InlineData(0x00, false)]
    public void PaperOut_OnlyFromAValidStatusByte(int status, bool paperOut) =>
        Assert.Equal(paperOut, EscPos.IsPaperOut((byte)status));

    [Fact]
    public void Threshold_BurnsBlackAndMidGrey_LeavesWhiteAndTransparent()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.SetPixel(0, 0, SKColors.Black);
        bitmap.SetPixel(1, 0, new SKColor(128, 128, 128));   // anti-aliased hairline: must not vanish
        bitmap.SetPixel(2, 0, SKColors.White);
        bitmap.SetPixel(3, 0, SKColors.Transparent);
        var mono = new MonoBitmap(4, 1);

        Raster.Into(bitmap, mono, 0, 1);

        Assert.Equal(new[] { true, true, false, false }, Enumerable.Range(0, 4).Select(x => mono[x, 0]).ToArray());
    }

    [Fact]
    public void Render_InStrips_HasNoSeam_AndScalesPageUnitsToDots()
    {
        // A box from (1, 1.5) to (3, 3.5) page units at 2 dots per unit = dots x 2..5, rows 3..6. Strips of 4 rows
        // split it at rows 4 and 8; the result must be the same as one big bitmap.
        var mono = Raster.Render(16, 10, 2f, canvas =>
        {
            using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
            canvas.DrawRect(1, 1.5f, 2, 2, fill);
        }, stripRows: 4);

        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 16; x++)
                Assert.True(mono[x, y] == (x is >= 2 and <= 5 && y is >= 3 and <= 6), $"dot ({x},{y})");
    }
}
