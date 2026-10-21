using System.Globalization;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application.Customers;

/// <summary>Indian phone numbers to E.164 in every common typed form, and rejecting what WhatsApp/SMS can't reach.</summary>
public class PhoneNumberTests
{
    [Theory]
    [InlineData("9876543210", "919876543210")]
    [InlineData("98765 43210", "919876543210")]
    [InlineData("98765-43210", "919876543210")]
    [InlineData("987-654-3210", "919876543210")]
    [InlineData(" 9876543210 ", "919876543210")]
    [InlineData("+91 98765 43210", "919876543210")]
    [InlineData("+919876543210", "919876543210")]
    [InlineData("+91-98765-43210", "919876543210")]
    [InlineData("(+91) 98765 43210", "919876543210")]
    [InlineData("919876543210", "919876543210")]
    [InlineData("09876543210", "919876543210")]
    [InlineData("0 98765 43210", "919876543210")]
    [InlineData("0091 98765 43210", "919876543210")]
    [InlineData("+91 (987) 654-3210", "919876543210")]
    [InlineData("6000000001", "916000000001")]
    [InlineData("7000012345", "917000012345")]
    [InlineData("8123456789", "918123456789")]
    public void Normalize_EveryCommonForm_ToE164WithoutPlus(string typed, string e164) =>
        Assert.Equal(e164, PhoneNumbers.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("987654321")]               // 9 digits
    [InlineData("98765432101")]             // 11 digits, no leading 0
    [InlineData("5876543210")]              // Indian mobiles start 6–9
    [InlineData("0000000000")]
    [InlineData("+91 40 2345 6789")]        // Hyderabad landline: no WhatsApp
    [InlineData("040 2345 6789")]
    [InlineData("+1 415 555 0100")]         // not Indian
    [InlineData("+44 7700 900123")]
    [InlineData("9876543210 9123456789")]   // two numbers in one box
    [InlineData("abcdefghij")]
    [InlineData("౯౮౭౬౫౪౩౨౧౦")]              // Telugu digits: not typed by the phone keypad; rejected, not misread
    public void Normalize_RejectsWhatIsNotAnIndianMobile(string? typed) => Assert.Null(PhoneNumbers.Normalize(typed));

    [Fact]
    public async Task SaveCustomer_StoresTheNormalisedNumber_RejectsABadOne_AndAllowsNone()
    {
        using var h = new AppHost();
        var ok = new Customer { Name = "  రవి  ", Phone = "+91 98765-43210" };
        Assert.True((await h.Sender.Send(new SaveCustomerCommand(ok))).IsSuccess);
        var saved = (await h.Sender.Send(new GetCustomerQuery(ok.Id)))!;
        Assert.Equal("919876543210", saved.Phone);
        Assert.Equal("రవి", saved.Name);

        var bad = new Customer { Name = "Sita", Phone = "12345" };
        Assert.Equal(ErrorCodes.PhoneInvalid, (await h.Sender.Send(new SaveCustomerCommand(bad))).Error.Code);
        Assert.Null(await h.Sender.Send(new GetCustomerQuery(bad.Id)));

        var none = new Customer { Name = "Lakshmi", Phone = "  " };
        Assert.True((await h.Sender.Send(new SaveCustomerCommand(none))).IsSuccess);
        Assert.Null((await h.Sender.Send(new GetCustomerQuery(none.Id)))!.Phone);

        Assert.Equal(ErrorCodes.ValidationFailed, (await h.Sender.Send(new SaveCustomerCommand(new Customer { Name = " ", Phone = "9876543210" }))).Error.Code);
    }
}

/// <summary>The shop's UPI QR: real app payloads parse or are rejected cleanly; the bill link keeps pa/pn/mc and sets am.</summary>
public class UpiPayloadTests
{
    [Theory]
    // PhonePe merchant sticker
    [InlineData("upi://pay?pa=Q123456789@ybl&pn=PhonePeMerchant&mc=0000&mode=02&purpose=00", "Q123456789@ybl", "PhonePeMerchant", "0000")]
    // Google Pay personal
    [InlineData("upi://pay?pa=ramukirana@okicici&pn=Ramu%20Kirana&aid=uGICAgIDXrNjVHQ", "ramukirana@okicici", "Ramu Kirana", null)]
    // Paytm for Business
    [InlineData("upi://pay?pa=paytmqr2810050501011ab12cd34@paytm&pn=Paytm%20Merchant&mc=5499&tr=PAYTM1234&paytmqr=281005050101AB12CD34", "paytmqr2810050501011ab12cd34@paytm", "Paytm Merchant", "5499")]
    // BHIM with empty optional fields
    [InlineData("upi://pay?pa=sita.med@upi&pn=Sita%20Medicals&mc=5912&tid=&tr=&tn=&am=&cu=INR&url=", "sita.med@upi", "Sita Medicals", "5912")]
    // Upper-case keys and scheme, any order
    [InlineData("UPI://PAY?MC=5411&PN=RAMU&PA=RAMU@YBL", "RAMU@YBL", "RAMU", "5411")]
    // '@' percent-encoded, '+' for space, '&' encoded inside the name
    [InlineData("upi://pay?pa=ramu%40ybl&pn=Ramu+%26+Sons", "ramu@ybl", "Ramu & Sons", null)]
    // Surrounding whitespace / newline from the scanner
    [InlineData("  upi://pay?pa=ramu-store@ybl&pn=Ramu\n", "ramu-store@ybl", "Ramu", null)]
    // Blank name and merchant code become null, not ""
    [InlineData("upi://pay?pa=ramu@ybl&pn=%20&mc=", "ramu@ybl", null, null)]
    public void Parse_RealAppPayloads(string scanned, string pa, string? pn, string? mc)
    {
        var qr = UpiQr.Parse(scanned);
        Assert.NotNull(qr);
        Assert.Equal(pa, qr.PayeeAddress);
        Assert.Equal(pn, qr.PayeeName);
        Assert.Equal(mc, qr.MerchantCode);
    }

    [Fact]
    public void Parse_ATeluguPayeeName_EncodedAsUtf8()
    {
        var qr = UpiQr.Parse("upi://pay?pa=ramu@ybl&pn=" + Uri.EscapeDataString("రాము కిరాణా"));
        Assert.Equal("రాము కిరాణా", qr!.PayeeName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("upi://pay")]
    [InlineData("upi://pay?")]
    [InlineData("upi://pay?pa=")]
    [InlineData("upi://pay?pa=ramu")]                          // no handle
    [InlineData("upi://pay?pa=ramu@")]
    [InlineData("upi://pay?pa=@ybl")]
    [InlineData("upi://pay?pa=r@ybl")]                         // 1-char user
    [InlineData("upi://pay?pa=ra%20mu@ybl")]                   // space in the VPA
    [InlineData("upi://pay?pa=ramu%26am%3D1@ybl")]             // smuggled parameter inside pa
    [InlineData("upi://pay?pa=ramu@ybl.com")]                  // e-mail, not a VPA
    [InlineData("upi://mandate?pa=ramu@ybl&pn=Ramu")]          // autopay mandate, not a payment QR
    [InlineData("upi:/pay?pa=ramu@ybl")]
    [InlineData("https://paytm.me/ab-1234")]
    [InlineData("https://wa.me/919876543210")]
    [InlineData("tel:9876543210")]
    [InlineData("8901725133979")]                              // a product barcode
    [InlineData("000201010211021645999900000000000415522019000000000061600000000000000000270015ramu@ybl5204541153033565802IN5904Ramu6009Hyderabad6304ABCD")]   // BharatQR (EMV) — see T3-5
    [InlineData("upi://pay?pa=ramu@ybl%ZZ&pn=Ramu")]           // broken escape: rejected, never throws
    public void Parse_RejectsCleanly_NeverThrows(string? scanned) => Assert.Null(UpiQr.Parse(scanned));

    [Theory]
    [InlineData(1L, "0.01")]
    [InlineData(5L, "0.05")]
    [InlineData(100L, "1.00")]
    [InlineData(29_400L, "294.00")]
    [InlineData(12_345_600L, "123456.00")]      // never "1,23,456.00" inside the link
    [InlineData(1_000_000_000L, "10000000.00")]
    public void Amount_IsRupeesWithTwoDecimals_InvariantCulture(long paise, string am)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-IN", "de-DE", "fr-FR" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                var link = UpiLink.Build("ramu@okaxis", "Ramu", new Paise(paise));
                Assert.Contains("&am=" + am + "&", link, StringComparison.Ordinal);
            }
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void ForShop_KeepsPayeeNameAndMerchantCode_ReplacesTheQrsOwnAmountRefAndNote()
    {
        var shop = new ShopProfile
        {
            Name = "Shop name typed at setup", UpiId = "typed@okaxis",
            UpiQrPayload = "upi://pay?pa=Q123456789@ybl&pn=Ramu%20%26%20Sons&mc=5411&tr=OLDREF&am=999.00&tn=old%20note&cu=INR",
        };
        var link = UpiLink.ForShop(shop, new Paise(29_450), "A-12");
        Assert.StartsWith("upi://pay?pa=Q123456789@ybl&pn=Ramu%20%26%20Sons&am=294.50&cu=INR&tn=A-12", link, StringComparison.Ordinal);
        Assert.Contains("&mc=5411", link, StringComparison.Ordinal);
        Assert.DoesNotContain("tr=", link, StringComparison.Ordinal);
        Assert.DoesNotContain("999", link, StringComparison.Ordinal);
        Assert.DoesNotContain("old", link, StringComparison.Ordinal);
        Assert.DoesNotContain("typed@okaxis", link, StringComparison.Ordinal);
        Assert.Single(link.Split('&'), p => p.StartsWith("am=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("upi://pay?pa=Q123456789@ybl&pn=PhonePeMerchant&mc=0000&mode=02")]
    [InlineData("upi://pay?pa=ramu%40ybl&pn=Ramu+%26+Sons%20%231")]
    [InlineData("UPI://PAY?PN=%E0%B0%B0%E0%B0%BE%E0%B0%AE%E0%B1%81&PA=ramu@okhdfcbank")]
    [InlineData("upi://pay?pa=sita.med@upi&pn=Sita%20Medicals&mc=5912&tid=&tr=&tn=&am=&cu=INR&url=")]
    public void ForShop_RoundTrips_TheScannedPayee(string scanned)
    {
        var own = UpiQr.Parse(scanned)!;
        var link = UpiLink.ForShop(new ShopProfile { Name = "x", UpiId = "x@ybl", UpiQrPayload = scanned }, new Paise(12_300), "A-1");
        var back = UpiQr.Parse(link)!;
        Assert.Equal(own.PayeeAddress, back.PayeeAddress);
        Assert.Equal(own.PayeeName, back.PayeeName);
        Assert.Equal(own.MerchantCode, back.MerchantCode);
        Assert.DoesNotContain(" ", link, StringComparison.Ordinal);
        Assert.DoesNotContain("#", link, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EscapesTeluguNameAndSpecialCharacters_ButNotThePayeeAddress()
    {
        var link = UpiLink.Build("  ramu@okaxis ", "రాము & సన్స్ #1", new Paise(100), "A/12 #3");
        Assert.StartsWith("upi://pay?pa=ramu@okaxis&pn=", link, StringComparison.Ordinal);
        Assert.DoesNotContain("%40", link, StringComparison.Ordinal);   // several UPI apps reject an escaped '@'
        Assert.DoesNotContain("#", link, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", link, StringComparison.Ordinal);
        Assert.Equal(4, link.Count(c => c == '&'));                     // pa&pn&am&cu&tn: the name's '&' is escaped
        var parsed = UpiQr.Parse(link)!;
        Assert.Equal("రాము & సన్స్ #1", parsed.PayeeName);
        Assert.EndsWith("&tn=" + Uri.EscapeDataString("A/12 #3"), link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShopProfile_QrImage_UpTo2Mb_IsKept_OneByteMoreIsRejected()
    {
        using var h = new AppHost();
        ShopProfile Profile(int bytes) => new()
        {
            Name = "Ramu", UpiId = "ramu@ybl", UpiQrPayload = "upi://pay?pa=ramu@ybl&pn=Ramu", UpiQrImage = new byte[bytes],
        };
        Assert.True((await h.Sender.Send(new SaveShopProfileCommand(Profile(UpiQr.MaxImageBytes)))).IsSuccess);
        Assert.Equal(ErrorCodes.UpiQrImageTooLarge, (await h.Sender.Send(new SaveShopProfileCommand(Profile(UpiQr.MaxImageBytes + 1)))).Error.Code);
        Assert.Equal(UpiQr.MaxImageBytes, (await h.Sender.Send(new GetShopProfileQuery()))!.UpiQrImage!.Length);   // the good one stays
        Assert.Equal(2 * 1024 * 1024, UpiQr.MaxImageBytes);
    }

    [Fact]
    public async Task ShopProfile_ABharatQrOrGarbagePayload_IsRejectedWithAClearCode()
    {
        using var h = new AppHost();
        foreach (var payload in new[] { "000201010211265", "upi://pay?pn=NoPayee", "https://example.com" })
        {
            var r = await h.Sender.Send(new SaveShopProfileCommand(new ShopProfile { Name = "Ramu", UpiId = "ramu@ybl", UpiQrPayload = payload }));
            Assert.Equal(ErrorCodes.UpiQrInvalid, r.Error.Code);
        }
    }
}

/// <summary>
/// The wa.me link as ReceiptShare.OpenWhatsAppAsync (Angadi.Maui) builds it, mirrored here because the Maui project
/// is not referenced by tests (see T3-6): Telugu, ₹, newlines, '&amp;', '#', '+', '%' must all survive into ?text=.
/// </summary>
public class WhatsAppLinkTests
{
    private static Uri MirrorOfOpenWhatsApp(string phoneE164, string message) =>
        new("https://wa.me/" + phoneE164 + "?text=" + Uri.EscapeDataString(message));

    [Theory]
    [InlineData("నమస్కారం రవి గారు, మీ బాకీ ₹1,23,456.50.\nతీసుకున్న ఉద్దెర:\n• 19 అక్టోబర్ — ₹40.00")]
    [InlineData("Ramu & Sons #1 — 50% off + free? a=b&c=d")]
    [InlineData("line1\r\nline2\tTab ‘quotes’ \"dq\" <tag> 100%")]
    public void Text_SurvivesTheLink_Exactly(string message)
    {
        var uri = MirrorOfOpenWhatsApp("919876543210", message);
        Assert.Equal("wa.me", uri.Host);
        Assert.Equal("/919876543210", uri.AbsolutePath);
        Assert.Equal("", uri.Fragment);                                  // a '#' never cuts the message short
        var query = uri.AbsoluteUri[(uri.AbsoluteUri.IndexOf('?', StringComparison.Ordinal) + 1)..];
        Assert.StartsWith("text=", query, StringComparison.Ordinal);
        var encoded = query["text=".Length..];
        Assert.DoesNotContain("&", encoded, StringComparison.Ordinal);   // one parameter only
        Assert.DoesNotContain(" ", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("+", encoded, StringComparison.Ordinal);   // '+' would read back as a space
        Assert.Equal(message, Uri.UnescapeDataString(encoded));
    }
}

/// <summary>Sending a bill to the bill's customer, and sharing the receipt PDF: content and every failure path.</summary>
public class SendBillTests : IDisposable
{
    private readonly ScriptedShare _share = new();
    private readonly AppHost _h;

    public SendBillTests()
    {
        _h = new AppHost(configure: s => s.AddSingleton<IReceiptShare>(_share));
        _h.Clock.Now = new DateTimeOffset(2026, 10, 19, 11, 0, 0, TimeSpan.FromHours(5.5));
    }

    public void Dispose() => _h.Dispose();

    private ILocalizer Text => _h.Services.GetRequiredService<ILocalizer>();

    private async Task<Customer> CustomerAsync(string name, string? phone)
    {
        var c = new Customer { Name = name, Phone = phone };
        Assert.True((await _h.Sender.Send(new SaveCustomerCommand(c))).IsSuccess);
        return c;
    }

    private async Task<FinalizedBill> SellAsync(TenderType tender, Guid? customerId, params (Item item, long qtyMilli)[] lines)
    {
        var draft = AppHost.Draft(tender, lines);
        draft.CustomerId = customerId;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.Error.Code);
        return r.Value;
    }

    [Theory]
    [InlineData(UiLanguage.Telugu)]
    [InlineData(UiLanguage.English)]
    public async Task UdhaarBill_Message_HasTheBillItemsAndTheirDues_ToTheRegisteredNumber(UiLanguage language)
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var milk = await _h.AddItemAsync("పాలు", 3000, unit: Unit.Pack);
        var ravi = await CustomerAsync("రవి", "0 98765 43210");
        await SellAsync(TenderType.Udhaar, ravi.Id, (milk, 1000));                       // ₹30 earlier
        var bill = await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1500), (milk, 2000));   // ₹90 + ₹60
        Text.Language = language;

        Assert.True((await _h.Sender.Send(new SendBillToCustomerCommand(bill.BillId))).IsSuccess);
        var (phone, message) = Assert.Single(_share.WhatsApp);
        Assert.Equal("919876543210", phone);
        var expected = string.Join("\n",
            Text.Format("receipt.customer.message", "రవి", "రాము కిరాణా", bill.Number, "₹150.00", Text["tender.udhaar"]),
            $"• బియ్యం 1.5 {Text.UnitLabel(Unit.Kilogram)} — ₹90.00",
            $"• పాలు 2 {Text.UnitLabel(Unit.Pack)} — ₹60.00",
            Text.Format("receipt.customer.balance", "₹180.00"));
        Assert.Equal(expected, message);
        Assert.DoesNotContain("{", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CashBill_ForACustomerWithNoDues_HasNoBalanceLine()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("Rice", 6000);
        var ravi = await CustomerAsync("Ravi", "9876543210");
        var bill = await SellAsync(TenderType.Cash, ravi.Id, (rice, 1000));
        Text.Language = UiLanguage.English;
        Assert.True((await _h.Sender.Send(new SendBillToCustomerCommand(bill.BillId))).IsSuccess);
        var message = Assert.Single(_share.WhatsApp).Message;
        Assert.Contains("(Cash)", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Text["receipt.customer.balance"].Split('{')[0], message, StringComparison.Ordinal);
    }

    [Fact(Skip = "BUG T3-3: a bill with more than 15 lines is cut to 15 items with no '…', so the items don't add up to the total")]
    public async Task LongBill_SaysThereAreMoreItems()
    {
        await _h.SeedShopAsync();
        var items = new List<(Item, long)>();
        for (var i = 0; i < 17; i++) items.Add((await _h.AddItemAsync("Item " + i, 1000 + i), 1000));
        var ravi = await CustomerAsync("Ravi", "9876543210");
        var bill = await SellAsync(TenderType.Cash, ravi.Id, [.. items]);
        Assert.True((await _h.Sender.Send(new SendBillToCustomerCommand(bill.BillId))).IsSuccess);
        var message = Assert.Single(_share.WhatsApp).Message;
        Assert.Contains("…", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailurePaths_AreResultErrors_NeverExceptions()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("Rice", 6000);
        var noPhone = await CustomerAsync("Lakshmi", null);
        var ravi = await CustomerAsync("Ravi", "9876543210");

        Assert.Equal(ErrorCodes.BillNotFound, (await _h.Sender.Send(new SendBillToCustomerCommand(Guid.NewGuid()))).Error.Code);
        var walkIn = await SellAsync(TenderType.Cash, null, (rice, 1000));
        Assert.Equal(ErrorCodes.CustomerNotFound, (await _h.Sender.Send(new SendBillToCustomerCommand(walkIn.BillId))).Error.Code);
        var lakshmis = await SellAsync(TenderType.Cash, noPhone.Id, (rice, 1000));
        Assert.Equal(ErrorCodes.CustomerNoPhone, (await _h.Sender.Send(new SendBillToCustomerCommand(lakshmis.BillId))).Error.Code);
        Assert.Empty(_share.WhatsApp);

        var bill = await SellAsync(TenderType.Upi, ravi.Id, (rice, 1000));
        _share.Behaviour = ShareBehaviour.Fails;
        Assert.Equal(ErrorCodes.ShareFailed, (await _h.Sender.Send(new SendBillToCustomerCommand(bill.BillId))).Error.Code);
        _share.Behaviour = ShareBehaviour.Throws;
        Assert.Equal(ErrorCodes.Unexpected, (await _h.Sender.Send(new SendBillToCustomerCommand(bill.BillId))).Error.Code);
        Assert.Equal(ErrorCodes.Unexpected, (await _h.Sender.Send(new ShareReceiptCommand(bill.BillId))).Error.Code);
    }

    [Fact]
    public async Task ShareReceipt_RendersThePdf_AndSharesItWithATitle()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("Rice", 6000);
        var bill = await SellAsync(TenderType.Cash, null, (rice, 1000));
        Text.Language = UiLanguage.English;
        Assert.True((await _h.Sender.Send(new ShareReceiptCommand(bill.BillId))).IsSuccess);
        var (file, title) = Assert.Single(_share.Files);
        Assert.EndsWith(bill.Number + ".pdf", file, StringComparison.Ordinal);
        Assert.Equal(Text.Format("receipt.share.text", "రాము కిరాణా", bill.Number, "₹60.00"), title);
        Assert.Equal(ErrorCodes.BillNotFound, (await _h.Sender.Send(new ShareReceiptCommand(Guid.NewGuid()))).Error.Code);
    }

    [Fact]
    public async Task ShareReceipt_WhenThePdfCannotBeMade_IsAResultError_AndNothingIsShared()
    {
        var share = new ScriptedShare();
        using var h = new AppHost(configure: s =>
        {
            s.AddSingleton<IReceiptShare>(share);
            s.AddSingleton<IReceiptRenderer, FailingRenderer>();
        });
        await h.SeedShopAsync();
        var rice = await h.AddItemAsync("Rice", 6000);
        var r = await h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 1000))));
        Assert.Equal(ErrorCodes.ReceiptRenderFailed, (await h.Sender.Send(new ShareReceiptCommand(r.Value.BillId))).Error.Code);
        Assert.Empty(share.Files);
    }
}
