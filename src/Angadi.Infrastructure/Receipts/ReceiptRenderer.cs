using System.Collections;
using System.Text;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Customers;
using Angadi.Domain.Shop;
using Microsoft.Extensions.Logging;
using QRCoder;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Angadi.Infrastructure.Receipts;

/// <summary>
/// Draws <see cref="ReceiptLayout"/> rows to a one-page, 80 mm wide PDF (FR-17, ADR-11).
/// Text is split into runs by font coverage: Telugu runs use Noto Sans Telugu and other runs use Noto Sans.
/// Each run is shaped with HarfBuzz, so conjuncts and vowel signs render correctly.
/// The page height comes from a measuring pass that runs the same code as the drawing pass with no canvas.
/// Thread-safety: HarfBuzz shapers are not thread-safe, so a gate serialises all renders.
/// </summary>
public sealed class ReceiptRenderer : IReceiptRenderer, IDisposable
{
    /// <summary>80 mm in PDF points (72 per inch).</summary>
    internal const float PageWidth = 80f / 25.4f * 72f;
    private const float Margin = 10f;
    private const float ContentWidth = PageWidth - 2 * Margin;
    private const float Gap = 6f;
    private const float Indent = 8f;

    private const float TitleSize = 13f;
    private const float HeadingSize = 10f;
    private const float BodySize = 8.5f;
    private const float SmallSize = 7f;
    private const float GrandSize = 11f;
    private const float VoidSize = 16f;
    private const float QrMaxSize = 128f;

    private readonly ReceiptFonts _fonts;
    private readonly string _outputDirectory;
    private readonly ILocalizer _text;
    private readonly ILogger<ReceiptRenderer> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Cached on first render, guarded by _gate.
    private Face? _telugu, _teluguBold, _latin, _latinBold;
    private bool _disposed;

    public ReceiptRenderer(ReceiptFonts fonts, string outputDirectory, ILocalizer text, ILogger<ReceiptRenderer> log)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(log);
        _fonts = fonts;
        _outputDirectory = outputDirectory;
        _text = text;
        _log = log;
    }

    public Task<Result<string>> RenderAsync(Bill bill, ShopProfile shop, CancellationToken ct = default)
        => RenderAsync(bill, shop, null, ct);

    public async Task<Result<string>> RenderAsync(Bill bill, ShopProfile shop, Customer? customer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(shop);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() => RenderCore(bill, shop, customer), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Result<string> RenderCore(Bill bill, ShopProfile shop, Customer? customer)
    {
        try
        {
            EnsureFaces();
            var rows = ReceiptLayout.Build(bill, shop, _text, customer);

            // Pass 1: measure. Pass 2: draw with the same code.
            var measure = new Pass(null);
            foreach (var row in rows) DrawRow(measure, row);
            var height = (float)Math.Ceiling(measure.Y + Margin);

            Directory.CreateDirectory(_outputDirectory);
            var path = Path.Combine(_outputDirectory, SafeFileName(bill.Number) + ".pdf");
            var temp = path + ".tmp";

            using (var file = File.Create(temp))
            using (var document = SKDocument.CreatePdf(file))
            {
                var canvas = document.BeginPage(PageWidth, height);
                canvas.Clear(SKColors.White);
                var draw = new Pass(canvas);
                foreach (var row in rows) DrawRow(draw, row);
                document.EndPage();
                document.Close();
            }

            File.Move(temp, path, overwrite: true);
            _log.LogInformation("Receipt rendered for bill {BillId} ({Rows} rows, {Height} pt)", bill.Id, rows.Count, height);
            return Result.Success(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bill id only: no customer name, phone or line items in logs.
            _log.LogError(ex, "Receipt render failed for bill {BillId}", bill.Id);
            return Result.Failure<string>(ErrorCodes.ReceiptRenderFailed, ex.GetType().Name);
        }
    }

    // ───────────────────────────── rows ─────────────────────────────

    private void DrawRow(Pass p, ReceiptRow row)
    {
        switch (row.Kind)
        {
            case ReceiptRowKind.Title:
                Centered(p, row.Text, bold: true, TitleSize);
                p.Y += 2;
                break;
            case ReceiptRowKind.Heading:
                p.Y += 3;
                Centered(p, row.Text, bold: true, HeadingSize);
                break;
            case ReceiptRowKind.Text:
                Centered(p, row.Text, bold: false, SmallSize);
                break;
            case ReceiptRowKind.Void:
                VoidMarker(p, row.Text);
                break;
            case ReceiptRowKind.Separator:
                Separator(p);
                break;
            case ReceiptRowKind.Columns:
                LeftRight(p, row.Text, row.Value, bold: true, SmallSize, 0);
                break;
            case ReceiptRowKind.Item:
                p.Y += 1;
                LeftRight(p, row.Text, row.Value, bold: false, BodySize, 0);
                if (row.Secondary.Length > 0) LeftRight(p, row.Secondary, "", bold: false, SmallSize, Indent);
                if (row.Middle.Length > 0) LeftRight(p, row.Middle, "", bold: false, SmallSize, Indent);
                break;
            case ReceiptRowKind.ItemNote:
                LeftRight(p, row.Text, row.Value, bold: false, SmallSize, Indent);
                break;
            case ReceiptRowKind.Field:
            case ReceiptRowKind.Total:
                LeftRight(p, row.Text, row.Value, bold: false, BodySize, 0);
                break;
            case ReceiptRowKind.GrandTotal:
                p.Y += 2;
                LeftRight(p, row.Text, row.Value, bold: true, GrandSize, 0);
                p.Y += 2;
                break;
            case ReceiptRowKind.TaxHeader:
                p.Y += 2;
                ThreeColumns(p, row.Text, row.Middle, row.Value, bold: true);
                break;
            case ReceiptRowKind.Tax:
                ThreeColumns(p, row.Text, row.Middle, row.Value, bold: false);
                break;
            case ReceiptRowKind.Qr:
                Qr(p, row.Text, row.Secondary);
                break;
            case ReceiptRowKind.Footer:
                p.Y += 2;
                Centered(p, row.Text, bold: true, BodySize);
                break;
        }
    }

    private void Centered(Pass p, string text, bool bold, float size)
    {
        foreach (var line in Wrap(text, bold, size, ContentWidth))
        {
            var width = Measure(line, bold, size);
            DrawLine(p, line, bold, size, (PageWidth - width) / 2);
            p.Y += LineHeight(size);
        }
    }

    /// <summary>Left text wraps in the space the right value leaves; the value sits on the first line.</summary>
    private void LeftRight(Pass p, string left, string right, bool bold, float size, float indent)
    {
        var rightWidth = right.Length > 0 ? Measure(right, bold, size) : 0f;
        var available = ContentWidth - indent - (rightWidth > 0 ? rightWidth + Gap : 0);
        var lines = Wrap(left, bold, size, available);
        for (var i = 0; i < lines.Count; i++)
        {
            DrawLine(p, lines[i], bold, size, Margin + indent);
            if (i == 0 && rightWidth > 0) DrawLine(p, right, bold, size, PageWidth - Margin - rightWidth);
            p.Y += LineHeight(size);
        }
    }

    /// <summary>GST table: slab left, taxable right-aligned at 68% of the width, tax right-aligned at the edge.</summary>
    private void ThreeColumns(Pass p, string a, string b, string c, bool bold)
    {
        const float size = SmallSize;
        var middleRight = Margin + ContentWidth * 0.68f;
        DrawLine(p, a, bold, size, Margin);
        DrawLine(p, b, bold, size, middleRight - Measure(b, bold, size));
        DrawLine(p, c, bold, size, PageWidth - Margin - Measure(c, bold, size));
        p.Y += LineHeight(size);
    }

    private void VoidMarker(Pass p, string text)
    {
        p.Y += 4;
        var width = Measure(text, true, VoidSize);
        var boxWidth = Math.Min(ContentWidth, width + 20);
        var boxHeight = LineHeight(VoidSize) + 6;
        if (p.Canvas is { } canvas)
        {
            using var stroke = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
            canvas.DrawRect((PageWidth - boxWidth) / 2, p.Y, boxWidth, boxHeight, stroke);
        }
        p.Y += 3;
        DrawLine(p, text, true, VoidSize, (PageWidth - width) / 2);
        p.Y += boxHeight - 3 + 4;
    }

    private static void Separator(Pass p)
    {
        p.Y += 3;
        if (p.Canvas is { } canvas)
        {
            using var dash = SKPathEffect.CreateDash([2f, 2f], 0f);
            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 0.5f, Style = SKPaintStyle.Stroke, PathEffect = dash };
            canvas.DrawLine(Margin, p.Y, PageWidth - Margin, p.Y, paint);
        }
        p.Y += 3;
    }

    /// <summary>UPI QR from QRCoder's module matrix (it includes the 4-module quiet zone). Dark runs per row are one rect each.</summary>
    private void Qr(Pass p, string payload, string caption)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        List<BitArray> matrix = data.ModuleMatrix;
        var n = matrix.Count;
        var module = (float)Math.Floor(Math.Min(ContentWidth, QrMaxSize) / n * 4) / 4;   // quarter-point grid
        var size = module * n;
        var x0 = (PageWidth - size) / 2;
        var y0 = p.Y;

        if (p.Canvas is { } canvas)
        {
            using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
            for (var r = 0; r < n; r++)
            {
                var bits = matrix[r];
                var c = 0;
                while (c < n)
                {
                    if (!bits[c]) { c++; continue; }
                    var start = c;
                    while (c < n && bits[c]) c++;
                    canvas.DrawRect(x0 + start * module, y0 + r * module, (c - start) * module, module, fill);
                }
            }
        }

        p.Y += size;
        if (caption.Length > 0) Centered(p, caption, bold: false, SmallSize);
    }

    // ───────────────────────────── text ─────────────────────────────

    private void DrawLine(Pass p, string text, bool bold, float size, float x)
    {
        if (p.Canvas is not { } canvas || text.Length == 0) return;
        var baseline = p.Y + Ascent(size);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        foreach (var (run, telugu) in SplitRuns(text, TeluguFace(bold).Has, LatinFace(bold).Has))
        {
            var face = telugu ? TeluguFace(bold) : LatinFace(bold);
            using var font = new SKFont(face.Typeface, size);
            canvas.DrawShapedText(face.Shaper, run, x, baseline, font, paint);
            x += face.Shaper.Shape(run, font).Width;
        }
    }

    private float Measure(string text, bool bold, float size)
    {
        var width = 0f;
        foreach (var (run, telugu) in SplitRuns(text, TeluguFace(bold).Has, LatinFace(bold).Has))
        {
            var face = telugu ? TeluguFace(bold) : LatinFace(bold);
            using var font = new SKFont(face.Typeface, size);
            width += face.Shaper.Shape(run, font).Width;
        }
        return width;
    }

    /// <summary>Greedy word wrap on spaces. A single word wider than the line is left whole.</summary>
    private List<string> Wrap(string text, bool bold, float size, float maxWidth)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && Measure(candidate, bold, size) > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        lines.Add(current);
        return lines;
    }

    /// <summary>Line height and ascent take the larger of the two fonts: Telugu needs room for stacked vowel signs.</summary>
    private float LineHeight(float size) => Math.Max(Spacing(_telugu!, size), Spacing(_latin!, size));

    private float Ascent(float size) => Math.Max(AscentOf(_telugu!, size), AscentOf(_latin!, size));

    private static float Spacing(Face face, float size)
    {
        using var font = new SKFont(face.Typeface, size);
        return font.GetFontMetrics(out _);
    }

    private static float AscentOf(Face face, float size)
    {
        using var font = new SKFont(face.Typeface, size);
        font.GetFontMetrics(out var metrics);
        return -metrics.Ascent;
    }

    /// <summary>
    /// Splits text into maximal runs for one font. Telugu block (U+0C00–0C7F) → Telugu font when it has the
    /// glyph. Anything else → Latin font when it has it, else Telugu when that has it, else Latin (tofu).
    /// Spaces and ZWJ/ZWNJ join the current run so shaping sees whole words.
    /// </summary>
    internal static List<(string Text, bool Telugu)> SplitRuns(string text, Func<int, bool> teluguHas, Func<int, bool> latinHas)
    {
        var runs = new List<(string, bool)>();
        var buffer = new StringBuilder();
        bool? current = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            bool? want = Rune.IsWhiteSpace(rune) || cp == 0x200C || cp == 0x200D
                ? null
                : cp is >= 0x0C00 and <= 0x0C7F
                    ? teluguHas(cp) || !latinHas(cp)
                    : !latinHas(cp) && teluguHas(cp);

            if (want is { } w && current is { } c && w != c)
            {
                runs.Add((buffer.ToString(), c));
                buffer.Clear();
                current = w;
            }
            else if (want is not null && current is null)
            {
                current = want;
            }
            buffer.Append(rune.ToString());
        }
        if (buffer.Length > 0) runs.Add((buffer.ToString(), current ?? false));
        return runs;
    }

    // ───────────────────────────── fonts ─────────────────────────────

    private Face TeluguFace(bool bold) => bold ? _teluguBold! : _telugu!;
    private Face LatinFace(bool bold) => bold ? _latinBold! : _latin!;

    private void EnsureFaces()
    {
        _telugu ??= Face.Load(_fonts.Telugu, "Telugu");
        _teluguBold ??= Face.Load(_fonts.TeluguBold, "TeluguBold");
        _latin ??= Face.Load(_fonts.Latin, "Latin");
        _latinBold ??= Face.Load(_fonts.LatinBold, "LatinBold");
    }

    private static string SafeFileName(string billNumber)
    {
        var name = string.IsNullOrWhiteSpace(billNumber) ? "receipt" : billNumber.Trim();
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        return name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _telugu?.Dispose();
        _teluguBold?.Dispose();
        _latin?.Dispose();
        _latinBold?.Dispose();
        _gate.Dispose();
    }

    /// <summary>A loaded TTF with its HarfBuzz shaper. The SKData is kept alive with the typeface.</summary>
    private sealed class Face : IDisposable
    {
        private readonly SKData _data;

        private Face(SKData data, SKTypeface typeface)
        {
            _data = data;
            Typeface = typeface;
            Shaper = new SKShaper(typeface);
        }

        public SKTypeface Typeface { get; }
        public SKShaper Shaper { get; }

        public bool Has(int codepoint) => Typeface.ContainsGlyph(codepoint);

        public static Face Load(Func<Stream> open, string name)
        {
            byte[] bytes;
            using (var stream = open() ?? throw new InvalidOperationException($"Font stream {name} is null."))
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }

            var data = SKData.CreateCopy(bytes);
            var typeface = SKTypeface.FromData(data);
            if (typeface is null)
            {
                data.Dispose();
                throw new InvalidOperationException($"Font {name} is not a valid TTF.");
            }
            return new Face(data, typeface);
        }

        public void Dispose()
        {
            Shaper.Dispose();
            Typeface.Dispose();
            _data.Dispose();
        }
    }

    /// <summary>One layout pass. Canvas is null while measuring.</summary>
    private sealed class Pass(SKCanvas? canvas)
    {
        public SKCanvas? Canvas { get; } = canvas;
        public float Y { get; set; } = Margin;
    }
}
