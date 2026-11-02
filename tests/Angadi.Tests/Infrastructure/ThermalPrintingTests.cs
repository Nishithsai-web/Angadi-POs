using Angadi.Application.Platform;
using Angadi.Application.Printing;
using Angadi.Infrastructure.Printing;
using Angadi.Infrastructure.Receipts;
using Angadi.Tests.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Angadi.Tests.Infrastructure;

/// <summary>v2 printing: the printer settings file and the thermal raster drawn by the receipt renderer.</summary>
public sealed class ThermalPrintingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "angadi-print-" + Guid.NewGuid().ToString("N"));

    public ThermalPrintingTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task SettingsFile_RoundTrips_AndBadContentMeansNoPrinter()
    {
        var path = Path.Combine(_folder, "printer.json");
        var store = new PrinterSettingsStore(path);
        Assert.Equal(PrinterSettings.None, await store.LoadAsync());

        var chosen = new PrinterSettings("AA:BB:CC:DD:EE:FF", "తెలుగు ప్రింటర్", PaperWidth.Mm80, true);
        await store.SaveAsync(chosen);
        Assert.Equal(chosen, await store.LoadAsync());
        Assert.False(File.Exists(path + ".tmp"));

        await File.WriteAllTextAsync(path, "{ not json");
        Assert.Equal(PrinterSettings.None, await store.LoadAsync());

        await File.WriteAllTextAsync(path, """{"address":"  ","name":"x","paper":80,"autoPrint":true}""");
        var noAddress = await store.LoadAsync();
        Assert.False(noAddress.IsSet);
        Assert.False(noAddress.AutoPrint);
        Assert.Null(noAddress.Name);

        await File.WriteAllTextAsync(path, """{"address":"AA:BB:CC:DD:EE:FF","paper":72}""");
        Assert.Equal(PaperWidth.Mm58, (await store.LoadAsync()).Paper);
    }

    [Theory]
    [InlineData(PaperWidth.Mm58, 384)]
    [InlineData(PaperWidth.Mm80, 576)]
    public async Task TestPage_IsACompleteJob_AtThePrinterWidth(PaperWidth paper, int dots)
    {
        using var host = new AppHost();
        using var renderer = new ReceiptRenderer(Fonts(), Path.Combine(_folder, "receipts"),
            host.Services.GetRequiredService<ILocalizer>(), NullLogger<ReceiptRenderer>.Instance);

        var job = await renderer.RenderTestPageAsync("రాము కిరాణా", paper);

        // HarfBuzz's native library is not among the test project's packages on every runner; the raster maths and
        // byte layout are covered by EscPosTests either way. When it is there, check the whole job.
        if (job.IsFailure && NativeMissing(job.Error.Detail)) return;
        Assert.True(job.IsSuccess, job.IsFailure ? job.Error.Code + " " + job.Error.Detail : "");
        var bytes = job.Value;
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1D, 0x76, 0x30, 0x00, (byte)(dots / 8), 0x00 }, bytes[..8]);
        Assert.Equal(EscPos.FeedAndCut(), bytes[^7..]);
        var rows = (bytes.Length - 2 - 7) / (dots / 8);
        Assert.InRange(rows, 150, 3000);
    }

    [Fact]
    public async Task Rows_DrawInk_WithinThePrintableWidth()
    {
        using var host = new AppHost();
        using var renderer = new ReceiptRenderer(Fonts(), Path.Combine(_folder, "receipts"),
            host.Services.GetRequiredService<ILocalizer>(), NullLogger<ReceiptRenderer>.Instance);
        IReadOnlyList<ReceiptRow> rows =
        [
            new ReceiptRow(ReceiptRowKind.Title) { Text = "శ్రీ లక్ష్మీ కిరాణా" },
            new ReceiptRow(ReceiptRowKind.Separator),
            new ReceiptRow(ReceiptRowKind.Item) { Text = "కందిపప్పు", Secondary = "Toor dal", Middle = "1 kg × ₹140.00", Value = "₹140.00" },
            new ReceiptRow(ReceiptRowKind.Qr) { Text = "upi://pay?pa=ramu@okaxis&am=140.00&cu=INR", Secondary = "UPI" },
            new ReceiptRow(ReceiptRowKind.GrandTotal) { Text = "మొత్తం", Value = "₹140" },
        ];

        MonoBitmap image;
        try
        {
            image = await Task.Run(() => renderer.RenderMono(rows, 384));
        }
        catch (Exception ex) when (NativeMissing(ex.GetType().Name))
        {
            return;   // see TestPage_IsACompleteJob_AtThePrinterWidth
        }

        Assert.Equal(384, image.Width);
        Assert.Contains(image.Bits, b => b != 0);
        // The 3 pt margins (8 dots) stay blank on both edges.
        for (var y = 0; y < image.Height; y++)
        {
            Assert.False(image[0, y], $"left edge ink at row {y}");
            Assert.False(image[383, y], $"right edge ink at row {y}");
        }
    }

    private static bool NativeMissing(string? detail) =>
        detail is "DllNotFoundException" or "TypeInitializationException" or "EntryPointNotFoundException" or "BadImageFormatException";

    private static ReceiptFonts Fonts()
    {
        static Func<Stream> Open(string name) => () => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fonts", name));
        return new ReceiptFonts(Open("NotoSansTelugu-Regular.ttf"), Open("NotoSansTelugu-Bold.ttf"), Open("NotoSans-Regular.ttf"), Open("NotoSans-Bold.ttf"));
    }
}
