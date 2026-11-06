using System.Text;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;

namespace Angadi.Tests.Application;

/// <summary>Spreadsheet export: safe cells, readable Telugu, correct money, and the range guard.</summary>
public sealed class CsvExportTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData("rice", "rice")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("", "")]
    public void Field_QuotesAndDefusesFormulas(string input, string expected) => Assert.Equal(expected, Csv.Field(input));

    [Fact]
    public void Field_NullIsEmpty() => Assert.Equal("", Csv.Field(null));

    [Theory]
    [InlineData(0, "0.00")]
    [InlineData(5, "0.05")]
    [InlineData(12_345, "123.45")]
    [InlineData(-250, "-2.50")]
    public void Money_IsWholePaiseWithTwoDecimals(long paise, string expected) => Assert.Equal(expected, Csv.Money(new Paise(paise)));

    [Fact]
    public async Task Sales_ExportHasABomHeaderAndOneRowPerBill()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 2000))))).IsSuccess);
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Upi, (rice, 1000))))).IsSuccess);

        var made = await _h.Sender.Send(new ExportCsvCommand(CsvKind.Sales, _h.Clock.Today, _h.Clock.Today));
        Assert.True(made.IsSuccess, made.Error.Code);
        var bytes = await File.ReadAllBytesAsync(made.Value);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);   // UTF-8 BOM so Excel reads Telugu
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Bill,Date,Total,Payment,Status", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, l => l.Contains(",120.00,Cash,", StringComparison.Ordinal));   // 2 kg x ₹60
    }

    [Fact]
    public async Task Dues_AndStock_ExportTheirRows_AndNamesCannotRunAsFormulas()
    {
        await _h.SeedShopAsync();
        var item = await _h.AddItemAsync("=HYPERLINK(\"x\")", 6000);
        var c = new Customer { Name = "Lakshmi", Phone = "9000011223" };
        await _h.Sender.Send(new SaveCustomerCommand(c));
        var draft = AppHost.Draft(TenderType.Udhaar, (item, 1000));
        draft.CustomerId = c.Id;
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(draft))).IsSuccess);

        var dues = await File.ReadAllTextAsync((await _h.Sender.Send(new ExportCsvCommand(CsvKind.Dues, _h.Clock.Today, _h.Clock.Today))).Value);
        Assert.Contains("Lakshmi,", dues, StringComparison.Ordinal);
        Assert.Contains(",60.00", dues, StringComparison.Ordinal);

        var stock = await File.ReadAllTextAsync((await _h.Sender.Send(new ExportCsvCommand(CsvKind.Stock, _h.Clock.Today, _h.Clock.Today))).Value);
        Assert.Contains("Item,Unit,Stock,Value,Status", stock, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", stock, StringComparison.Ordinal);   // defused and quoted
    }

    [Fact]
    public async Task BadRanges_AreRefused()
    {
        var today = _h.Clock.Today;
        Assert.Equal(ErrorCodes.ExportRangeInvalid, (await _h.Sender.Send(new ExportCsvCommand(CsvKind.Sales, today, today.AddDays(-1)))).Error.Code);
        Assert.Equal(ErrorCodes.ExportRangeInvalid, (await _h.Sender.Send(new ExportCsvCommand(CsvKind.Sales, today.AddDays(-400), today))).Error.Code);
    }
}
