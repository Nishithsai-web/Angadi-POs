using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Domain.Common;
using Angadi.Domain.Shop;
using Angadi.Tests.Application;

namespace Angadi.Tests.Domain;

/// <summary>The shop's own UPI QR: read it, and pay exactly that payee with the bill amount filled in.</summary>
public class UpiQrTests
{
    [Theory]
    [InlineData("upi://pay?pa=ramu-store@ybl&pn=Ramu%20Kirana&mc=5411&tr=X1", "ramu-store@ybl", "Ramu Kirana", "5411")]
    [InlineData("UPI://PAY?PN=Sita+Medicals&PA=sita.med@okhdfcbank", "sita.med@okhdfcbank", "Sita Medicals", null)]
    public void Parse_ReadsPayeeNameAndMerchantCode(string scanned, string pa, string? pn, string? mc)
    {
        var qr = UpiQr.Parse(scanned)!;
        Assert.Equal(pa, qr.PayeeAddress);
        Assert.Equal(pn, qr.PayeeName);
        Assert.Equal(mc, qr.MerchantCode);
    }

    [Theory]
    [InlineData("https://example.com/pay")]
    [InlineData("upi://pay?pn=NoAddress")]
    [InlineData("8901725133979")]
    [InlineData("")]
    public void Parse_RejectsWhatIsNotAUpiPaymentQr(string scanned) => Assert.Null(UpiQr.Parse(scanned));

    [Fact]
    public void ForShop_UsesTheOwnersQr_WithTheBillAmount_AndDropsItsOldTransactionRef()
    {
        var shop = new ShopProfile { Name = "Ramu", UpiId = "ramu-store@ybl", UpiQrPayload = "upi://pay?pa=ramu-store@ybl&pn=Ramu%20Kirana&mc=5411&tr=X1" };
        var link = UpiLink.ForShop(shop, new Paise(29_400), "A-12");
        Assert.StartsWith("upi://pay?pa=ramu-store@ybl&pn=Ramu%20Kirana&am=294.00&cu=INR", link, StringComparison.Ordinal);
        Assert.Contains("mc=5411", link, StringComparison.Ordinal);
        Assert.DoesNotContain("tr=X1", link, StringComparison.Ordinal);
    }

    [Fact]
    public void ForShop_WithoutAScannedQr_UsesTheTypedUpiId() =>
        Assert.StartsWith("upi://pay?pa=ramu@okaxis&", UpiLink.ForShop(new ShopProfile { Name = "Ramu", UpiId = "ramu@okaxis" }, new Paise(100)), StringComparison.Ordinal);

    [Fact]
    public async Task ShopProfile_KeepsTheQrAndPhoto_AndRejectsABadQr()
    {
        using var h = new AppHost();
        var photo = new byte[] { 137, 80, 78, 71, 1, 2, 3 };
        var ok = await h.Sender.Send(new SaveShopProfileCommand(new ShopProfile
        {
            Name = "Ramu", UpiId = "ramu-store@ybl", UpiQrPayload = "upi://pay?pa=ramu-store@ybl&pn=Ramu", UpiQrImage = photo,
        }));
        Assert.True(ok.IsSuccess, ok.Error.Code);
        var saved = (await h.Sender.Send(new GetShopProfileQuery()))!;
        Assert.Equal(photo, saved.UpiQrImage);
        Assert.NotNull(saved.UpiQrPayload);

        var bad = await h.Sender.Send(new SaveShopProfileCommand(new ShopProfile { Name = "Ramu", UpiId = "ramu@okaxis", UpiQrPayload = "hello" }));
        Assert.Equal(ErrorCodes.UpiQrInvalid, bad.Error.Code);
    }
}
