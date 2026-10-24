using Angadi.Application.Printing;
using SkiaSharp;

namespace Angadi.Infrastructure.Printing;

/// <summary>
/// A 1-bit image in ESC/POS raster order: rows top to bottom, <see cref="BytesPerRow"/> bytes per row, the most
/// significant bit is the leftmost dot, 1 = black (burned).
/// </summary>
public sealed class MonoBitmap
{
    public MonoBitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        BytesPerRow = (width + 7) / 8;
        Bits = new byte[BytesPerRow * height];
    }

    public int Width { get; }
    public int Height { get; }
    public int BytesPerRow { get; }
    public byte[] Bits { get; }

    public bool this[int x, int y]
    {
        get => (Bits[y * BytesPerRow + x / 8] & (0x80 >> (x % 8))) != 0;
        set
        {
            var i = y * BytesPerRow + x / 8;
            var mask = (byte)(0x80 >> (x % 8));
            Bits[i] = value ? (byte)(Bits[i] | mask) : (byte)(Bits[i] & ~mask);
        }
    }
}

/// <summary>Colour bitmap → 1-bit raster (fixed threshold on luminance, transparent counts as white paper).</summary>
public static class Raster
{
    /// <summary>
    /// Luminance below this burns a dot. Higher than 128 on purpose: anti-aliased hairlines (the dashed separator,
    /// thin Telugu strokes at 58 mm) land between two pixels at ~50% grey and must not vanish.
    /// </summary>
    public const int DefaultThreshold = 160;

    /// <summary>Converts <paramref name="bitmap"/> into rows <paramref name="targetTop"/>.. of <paramref name="target"/>.</summary>
    public static void Into(SKBitmap bitmap, MonoBitmap target, int targetTop, int rows, int threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(target);
        if (bitmap.ColorType != SKColorType.Rgba8888 || bitmap.AlphaType == SKAlphaType.Unpremul)
            throw new ArgumentException("expects an Rgba8888 premultiplied/opaque bitmap", nameof(bitmap));
        var pixels = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        var width = Math.Min(bitmap.Width, target.Width);
        rows = Math.Min(rows, Math.Min(bitmap.Height, target.Height - targetTop));
        for (var y = 0; y < rows; y++)
        {
            var row = pixels.Slice(y * rowBytes, width * 4);
            for (var x = 0; x < width; x++)
            {
                int r = row[x * 4], g = row[x * 4 + 1], b = row[x * 4 + 2], a = row[x * 4 + 3];
                // Premultiplied colour over white paper: c + (255 − a).
                var luma = (299 * (r + 255 - a) + 587 * (g + 255 - a) + 114 * (b + 255 - a)) / 1000;
                if (luma < threshold) target[x, targetTop + y] = true;
            }
        }
    }

    public static MonoBitmap FromBitmap(SKBitmap bitmap, int threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var mono = new MonoBitmap(bitmap.Width, bitmap.Height);
        Into(bitmap, mono, 0, bitmap.Height, threshold);
        return mono;
    }
}

/// <summary>
/// The few ESC/POS commands Angadi sends. Every 58/80 mm thermal printer sold for Indian shops accepts these:
/// ESC @ (reset), GS v 0 (raster bit image, normal density), ESC d (feed n lines), GS V 66 0 (feed to the cutter and
/// partial cut — printers without a cutter ignore it and the feed leaves room to tear).
/// </summary>
public static class EscPos
{
    /// <summary>Rows per GS v 0 command: small enough for the input buffer of cheap printers.</summary>
    public const int BandRows = 96;
    public const byte FeedLinesBeforeCut = 4;

    public static int DotsFor(PaperWidth paper) => paper == PaperWidth.Mm80 ? 576 : 384;

    public static byte[] Initialize() => [0x1B, 0x40];

    public static byte[] FeedAndCut(byte lines = FeedLinesBeforeCut) => [0x1B, 0x64, lines, 0x1D, 0x56, 0x42, 0x00];

    /// <summary>GS v 0 m xL xH yL yH d1…dk for rows [<paramref name="firstRow"/>, +<paramref name="rows"/>).</summary>
    public static byte[] RasterBand(MonoBitmap image, int firstRow, int rows)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(firstRow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        if (firstRow + rows > image.Height) throw new ArgumentOutOfRangeException(nameof(rows));
        if (image.BytesPerRow > 0xFFFF || rows > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(image));
        var x = image.BytesPerRow;
        var data = new byte[8 + x * rows];
        data[0] = 0x1D; data[1] = 0x76; data[2] = 0x30; data[3] = 0x00;
        data[4] = (byte)(x & 0xFF); data[5] = (byte)(x >> 8);
        data[6] = (byte)(rows & 0xFF); data[7] = (byte)(rows >> 8);
        Array.Copy(image.Bits, firstRow * x, data, 8, x * rows);
        return data;
    }

    /// <summary>The whole job: reset, the image in bands of <paramref name="bandRows"/>, feed, partial cut.</summary>
    public static byte[] Job(MonoBitmap image, int bandRows = BandRows)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bandRows);
        using var job = new MemoryStream();
        job.Write(Initialize());
        for (var y = 0; y < image.Height; y += bandRows)
            job.Write(RasterBand(image, y, Math.Min(bandRows, image.Height - y)));
        job.Write(FeedAndCut());
        return job.ToArray();
    }
}
