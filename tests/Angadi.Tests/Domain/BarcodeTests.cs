using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Domain.Common;
using Angadi.Domain.Items;
using Angadi.Tests.Application;

namespace Angadi.Tests.Domain;

/// <summary>Barcode normalization and check digits; attaching a scanned code to an existing item.</summary>
public class BarcodeTests
{
    [Theory]
    [InlineData("8901725133979", true)]     // EAN-13 (Indian packet, 890 prefix)
    [InlineData("8901725133978", false)]    // last digit off
    [InlineData("96385074", true)]          // EAN-8
    [InlineData("96385075", false)]
    [InlineData("SHOP-RICE-25", true)]      // shop-made code: accepted as typed
    public void CheckDigit(string code, bool valid) => Assert.Equal(valid, Barcodes.IsValid(Barcodes.Normalize(code)!));

    [Fact]
    public void UpcA_And_Ean13_AreTheSameCode()
    {
        Assert.Equal("0036000291452", Barcodes.Normalize("036000291452"));
        Assert.Equal("0036000291452", Barcodes.Normalize(" 0 36000 29145 2 "));
        Assert.True(Barcodes.IsValid("0036000291452"));
        Assert.Null(Barcodes.Normalize("   "));
    }

    [Fact]
    public async Task ScannedUnknownCode_CanBeAttachedToAnExistingItem_AndThenFindsIt()
    {
        using var h = new AppHost();
        var tea = await h.AddItemAsync("టీ పొడి", 12000, unit: Unit.Pack);
        Assert.True((await h.Sender.Send(new SetItemBarcodeCommand(tea.Id, "8901725133979"))).IsSuccess);
        Assert.Equal(tea.Id, (await h.Sender.Send(new GetItemByBarcodeQuery("8901725133979")))!.Id);

        var sugar = await h.AddItemAsync("చక్కెర", 4800);
        var dup = await h.Sender.Send(new SetItemBarcodeCommand(sugar.Id, "8901725133979"));
        Assert.Equal(ErrorCodes.BarcodeDuplicate, dup.Error.Code);
        Assert.Equal(ErrorCodes.BarcodeInvalid, (await h.Sender.Send(new SetItemBarcodeCommand(sugar.Id, "8901725133978"))).Error.Code);
    }

    [Fact]
    public async Task UpcScan_FindsItemSavedWithEanForm_AndViceVersa()
    {
        using var h = new AppHost();
        var item = new Item { TeluguName = "కోల్గేట్", EnglishName = "Toothpaste", Price = new Paise(5500), Barcode = "036000291452" };
        Assert.True((await h.Sender.Send(new SaveItemCommand(item))).IsSuccess);
        Assert.Equal("0036000291452", item.Barcode);   // stored in one form
        Assert.NotNull(await h.Sender.Send(new GetItemByBarcodeQuery("036000291452")));
        Assert.NotNull(await h.Sender.Send(new GetItemByBarcodeQuery("0036000291452")));
    }
}
