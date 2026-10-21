using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Domain.Items;
using Angadi.Tests.Application;

namespace Angadi.Tests.Domain;

/// <summary>
/// T4: barcode shapes a counter scanner (camera or USB) really reports — EAN-13 / EAN-8 / UPC-A, wrong check digits,
/// stray whitespace and newlines, and QR text, which must never be mistaken for a product code.
/// </summary>
public class BarcodeEdgeTests
{
    [Theory]
    [InlineData("8901030865275")]   // EAN-13, Indian (890) prefix
    [InlineData("8901700000012")]
    [InlineData("4006381333931")]
    [InlineData("5012345678900")]
    [InlineData("0000000000000")]   // check digit 0
    [InlineData("40170725")]        // EAN-8
    [InlineData("96385074")]
    public void ValidEan_IsAccepted(string code)
    {
        var normalized = Barcodes.Normalize(code);
        Assert.Equal(code, normalized);
        Assert.True(Barcodes.IsValid(normalized!));
    }

    [Theory]
    [InlineData("8901030865274")]   // every EAN-13 above, last digit off by one
    [InlineData("8901700000013")]
    [InlineData("4006381333930")]
    [InlineData("5012345678901")]
    [InlineData("40170724")]        // EAN-8
    [InlineData("96385070")]
    [InlineData("012345678904")]    // UPC-A with a wrong check digit (checked after it becomes EAN-13)
    public void WrongCheckDigit_IsRejected(string code) => Assert.False(Barcodes.IsValid(Barcodes.Normalize(code)!));

    [Fact]
    public void SwappedDigits_AreCaught()
    {
        // The classic keying slip: two neighbouring digits swapped (89 → 98).
        Assert.True(Barcodes.IsValid("8901030865275"));
        Assert.False(Barcodes.IsValid("9801030865275"));
    }

    [Theory]
    [InlineData("012345678905", "0012345678905")]
    [InlineData("036000291452", "0036000291452")]
    public void UpcA_BecomesEan13_AndStaysValid(string upc, string ean)
    {
        Assert.Equal(ean, Barcodes.Normalize(upc));
        Assert.True(Barcodes.IsValid(ean));
        Assert.Equal(upc, Barcodes.Alternate(ean));   // the old stored form is still looked up
    }

    [Theory]
    [InlineData("8901030865275\n")]          // USB scanners end with Enter
    [InlineData("\t8901030865275\r\n")]
    [InlineData(" 8901030865275 ")]
    [InlineData("890 1030 865 275")]         // typed from the packet in groups
    [InlineData("890-1030-865275")]
    [InlineData("8901030865275 ")]      // non-breaking space at the end
    public void WhitespaceAndHyphens_AreIgnored(string raw) => Assert.Equal("8901030865275", Barcodes.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    [InlineData(" - - ")]
    public void Nothing_IsNull(string? raw) => Assert.Null(Barcodes.Normalize(raw));

    [Theory]
    [InlineData("8901030865275")]   // EAN-13 not starting with 0: no other form
    [InlineData("40170725")]
    [InlineData("SHOPRICE25")]
    public void Alternate_OnlyForUpcShapedEan(string code) => Assert.Null(Barcodes.Alternate(code));

    [Theory]
    [InlineData("upi://pay?pa=ramstore@ybl&pn=Ram%20Store&mc=5411")]
    [InlineData("https://example.com/p/890103086527")]
    [InlineData("WIFI:S:shop;T:WPA;P:12345678;;")]
    public void QrText_IsNeverTurnedIntoAnEan(string qr)
    {
        var normalized = Barcodes.Normalize(qr)!;
        Assert.False(normalized.Length is 8 or 13 && normalized.All(char.IsAsciiDigit));
        Assert.Null(Barcodes.Alternate(normalized));
        Assert.StartsWith(qr[..4], normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void QrText_KeepsItsDigits_NoLeadingZeroIsAdded()
    {
        // A 12-digit run inside a QR must not get the UPC-A → EAN-13 "0" prefix: only a bare 12-digit code does.
        Assert.Equal("upi://pay?pa=919876543210@ybl", Barcodes.Normalize("upi://pay?pa=919876543210@ybl"));
    }

    [Fact]
    public async Task ScannedCode_WithTrailingNewline_FindsTheItem()
    {
        using var h = new AppHost();
        var item = await h.AddItemAsync("బిస్కెట్", 1000, unit: Unit.Pack);
        Assert.True((await h.Sender.Send(new SetItemBarcodeCommand(item.Id, "8901030865275"))).IsSuccess);
        Assert.Equal(item.Id, (await h.Sender.Send(new GetItemByBarcodeQuery("8901030865275\n")))?.Id);
        Assert.Equal(item.Id, (await h.Sender.Send(new GetItemByBarcodeQuery(" 890 1030 865 275 ")))?.Id);
        Assert.Null(await h.Sender.Send(new GetItemByBarcodeQuery("8901030865274")));
    }

    [Fact]
    public async Task SavingAnItem_WithAWrongCheckDigit_IsRefused()
    {
        using var h = new AppHost();
        var item = new Item { TeluguName = "సబ్బు", EnglishName = "Soap", Price = new Angadi.Domain.Common.Paise(3000), Barcode = "8901030865274" };
        var r = await h.Sender.Send(new SaveItemCommand(item));
        Assert.Equal(ErrorCodes.BarcodeInvalid, r.Error.Code);
    }

    // ------------------------------------------------------------------ real bugs (skipped until fixed)

    [Theory(Skip = "BUG T4-09: an 8-digit UPC-E (scanner has BarcodeFormats.Upce on) is checked as EAN-8 and rejected")]
    [InlineData("04252614", "0042100005264")]   // UPC-E of UPC-A 042100005264
    [InlineData("01234565", "0012345000065")]
    public void UpcE_IsAccepted_AndFindsTheSameItemAsItsUpcA(string upcE, string ean13)
    {
        Assert.True(Barcodes.IsValid(Barcodes.Normalize(upcE)!));
        Assert.True(Barcodes.IsValid(ean13));
        Assert.Equal(ean13, Barcodes.Normalize(upcE));   // one form per packet, like UPC-A → EAN-13
    }

    [Fact(Skip = "BUG T4-10: a UPI / URL QR scanned at the counter is accepted as an item barcode (and its hyphens stripped)")]
    public async Task UpiQr_CannotBecomeAnItemBarcode()
    {
        using var h = new AppHost();
        var item = await h.AddItemAsync("టీ పొడి", 12000, unit: Unit.Pack);
        var r = await h.Sender.Send(new SetItemBarcodeCommand(item.Id, "upi://pay?pa=ram-store@ybl&pn=Ram"));
        Assert.Equal(ErrorCodes.BarcodeInvalid, r.Error.Code);
    }
}
