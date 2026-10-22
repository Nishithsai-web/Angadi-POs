using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using Angadi.Infrastructure.Persistence;
using Angadi.Infrastructure.Receipts;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>
/// F1: GST receipts and money integrity — composition shops (A2-01), CGST/SGST (A2-02), no "scan to pay" on a paid
/// UPI receipt (A2-05), past-month voids (A2-14),
/// the customer on the receipt (A2-15), the bill prefix (A2-16), a commit that fails after the handler (A1-01),
/// quantity limits (A1-08) and unique pick labels (A1-06).
/// </summary>
public sealed class GstReceiptIntegrityTests : IDisposable
{
    private const string Gstin = "36ABCDE1234F1Z5";
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private readonly AppHost _h = new();

    public GstReceiptIntegrityTests()
    {
        _h.Subscription.IsPro = true;
        Text.Language = UiLanguage.English;
    }

    public void Dispose() => _h.Dispose();

    private ILocalizer Text => _h.Services.GetRequiredService<ILocalizer>();

    private async Task SeedShopAsync(string? gstin, bool composition = false, string prefix = "A")
    {
        var r = await _h.Sender.Send(new SaveShopProfileCommand(new ShopProfile
        {
            Name = "రాము కిరాణా", UpiId = "ramu@okaxis", Gstin = gstin, IsComposition = composition, BillPrefix = prefix,
        }));
        Assert.True(r.IsSuccess, r.Error.Code);
    }

    private async Task<ShopProfile> ShopAsync() => (await _h.Sender.Send(new GetShopProfileQuery()))!;

    private async Task<Bill> SellAsync(TenderType tender, Guid? customer, params (Item item, long qtyMilli)[] lines)
    {
        var draft = AppHost.Draft(tender, lines);
        draft.CustomerId = customer;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.Error.Code);
        return (await _h.Sender.Send(new GetBillQuery(r.Value.BillId)))!;
    }

    // ---------- A2-01 composition scheme ----------

    [Fact]
    public async Task A2_01_CompositionShop_IssuesBillOfSupply_WithGstinAndDeclaration_NoTax()
    {
        await SeedShopAsync(Gstin, composition: true);
        var shop = await ShopAsync();
        Assert.True(shop.IsComposition);                     // persisted
        Assert.False(shop.ChargesGst);

        var rice = await _h.AddItemAsync("బియ్యం", 6_250, GstSlab.Five);
        var bill = await SellAsync(TenderType.Cash, null, (rice, 2_000));
        Assert.False(bill.IsTaxInvoice);
        Assert.Empty(bill.TaxSplits);
        Assert.Equal(Paise.Zero, bill.TaxTotal);
        Assert.Equal(12_500, bill.GrandTotal.Value);         // the price is unchanged; only the document type differs

        var rows = ReceiptLayout.Build(bill, shop, Text);
        Assert.Contains(rows, r => r.Kind == ReceiptRowKind.Heading && r.Text == Text["bill.billofsupply"]);
        Assert.Contains(rows, r => r.Kind == ReceiptRowKind.Text && r.Text.Contains(Gstin, StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Text == Text["receipt.composition"]);
        Assert.Contains("not eligible to collect tax", Text["receipt.composition"], StringComparison.Ordinal);
        Assert.DoesNotContain(rows, r => r.Kind is ReceiptRowKind.Tax or ReceiptRowKind.TaxHeader);
        Assert.Empty(await _h.Sender.Send(new GetGstSummaryQuery(_h.Clock.Today, _h.Clock.Today)));
    }

    [Fact]
    public async Task A2_01_RegularGstShop_HasNoCompositionLine()
    {
        await SeedShopAsync(Gstin);
        var rice = await _h.AddItemAsync("బియ్యం", 6_250, GstSlab.Five);
        var bill = await SellAsync(TenderType.Cash, null, (rice, 1_000));
        Assert.True(bill.IsTaxInvoice);
        Assert.DoesNotContain(ReceiptLayout.Build(bill, await ShopAsync(), Text), r => r.Text == Text["receipt.composition"]);
    }

    // ---------- A2-02 CGST / SGST ----------

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(5L)]
    [InlineData(915L)]
    [InlineData(1264L)]
    public void A2_02_CgstAndSgst_AreHalves_RoundedHalfUp_AndAlwaysSumToTheTax(long tax)
    {
        var split = new TaxSplit(GstSlab.Eighteen, new Paise(10_000), new Paise(tax));
        Assert.Equal((tax + 1) / 2, split.Cgst.Value);
        Assert.Equal(tax, split.Cgst.Value + split.Sgst.Value);
        Assert.True(split.Cgst >= split.Sgst);
        Assert.Equal(900, split.HalfRateBasisPoints);
    }

    [Fact]
    public async Task A2_02_TaxInvoice_ShowsRatePerLine_AndCgstSgstPerSlab()
    {
        await SeedShopAsync(Gstin);
        var rice = await _h.AddItemAsync("Rice", 6_250, GstSlab.Five);
        var oil = await _h.AddItemAsync("Oil", 19_900, GstSlab.Eighteen, unit: Unit.Litre);
        var dal = await _h.AddItemAsync("Dal", 15_975, GstSlab.Zero);
        var bill = await SellAsync(TenderType.Cash, null, (rice, 1_000), (oil, 1_000), (dal, 1_000));

        var rows = ReceiptLayout.Build(bill, await ShopAsync(), Text);
        var items = rows.Where(r => r.Kind == ReceiptRowKind.Item).ToList();
        Assert.EndsWith("GST 5%", items[0].Middle, StringComparison.Ordinal);
        Assert.EndsWith("GST 18%", items[1].Middle, StringComparison.Ordinal);
        Assert.EndsWith("GST 0%", items[2].Middle, StringComparison.Ordinal);

        var tax = rows.Where(r => r.Kind == ReceiptRowKind.Tax).ToList();
        var five = bill.TaxSplits.Single(s => s.Slab == GstSlab.Five);
        var eighteen = bill.TaxSplits.Single(s => s.Slab == GstSlab.Eighteen);
        Assert.Contains(tax, r => r.Text.Trim() == "CGST 2.5%" && r.Value == IndianFormat.Rupees(five.Cgst));
        Assert.Contains(tax, r => r.Text.Trim() == "SGST 2.5%" && r.Value == IndianFormat.Rupees(five.Sgst));
        Assert.Contains(tax, r => r.Text.Trim() == "CGST 9%" && r.Value == IndianFormat.Rupees(eighteen.Cgst));
        Assert.Contains(tax, r => r.Text.Trim() == "SGST 9%" && r.Value == IndianFormat.Rupees(eighteen.Sgst));
        Assert.DoesNotContain(tax, r => r.Text.Trim().StartsWith("CGST 0%", StringComparison.Ordinal));   // nil-rated: no halves
        // The halves never change the totals.
        Assert.Equal(bill.TaxTotal.Value, bill.TaxSplits.Sum(s => s.Cgst.Value + s.Sgst.Value));

        var summary = await _h.Sender.Send(new GetGstSummaryQuery(_h.Clock.Today, _h.Clock.Today));
        Assert.All(summary, s => Assert.Equal(s.Tax.Value, s.Cgst.Value + s.Sgst.Value));
    }

    [Fact]
    public async Task A2_02_BillOfSupply_ShowsNoRatesOrHalves()
    {
        await SeedShopAsync(null);
        var rice = await _h.AddItemAsync("Rice", 6_250, GstSlab.Five);
        var bill = await SellAsync(TenderType.Cash, null, (rice, 1_000));
        var rows = ReceiptLayout.Build(bill, await ShopAsync(), Text);
        Assert.DoesNotContain(rows, r => r.Kind == ReceiptRowKind.Item && r.Middle.Contains('%'));
        Assert.DoesNotContain(rows, r => r.Kind == ReceiptRowKind.Tax);
    }

    // ---------- A2-05 QR only where money is still owed ----------

    [Theory]
    [InlineData(TenderType.Cash, false)]
    [InlineData(TenderType.Upi, false)]
    [InlineData(TenderType.Udhaar, true)]
    public async Task A2_05_ScanToPayQr_OnlyOnUdhaarReceipts(TenderType tender, bool shows)
    {
        await SeedShopAsync(null);
        var customer = (await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "Ravi" }))).Value;
        var rice = await _h.AddItemAsync("Rice", 6_250);
        var bill = await SellAsync(tender, tender == TenderType.Udhaar ? customer : null, (rice, 1_000));
        var shop = await ShopAsync();
        Assert.Equal(shows, ReceiptLayout.ShowsQr(bill, shop));
        Assert.Equal(shows, ReceiptLayout.Build(bill, shop, Text).Any(r => r.Kind == ReceiptRowKind.Qr));
    }

    // ---------- A2-14 voiding a bill from a filed month ----------

    [Fact]
    public async Task A2_14_VoidingLastMonthsBill_NeedsExplicitConfirmation()
    {
        await SeedShopAsync(Gstin);
        var rice = await _h.AddItemAsync("Rice", 6_250, stockMilli: 100_000);
        _h.Clock.Now = new DateTimeOffset(2026, 9, 30, 21, 0, 0, Ist);
        var september = await SellAsync(TenderType.Cash, null, (rice, 1_000));
        _h.Clock.Now = new DateTimeOffset(2026, 10, 1, 9, 0, 0, Ist);
        var october = await SellAsync(TenderType.Cash, null, (rice, 1_000));

        var refused = await _h.Sender.Send(new VoidBillCommand(september.Id, "returned"));
        Assert.Equal(ErrorCodes.BillVoidPastMonth, refused.Error.Code);
        Assert.Equal(BillStatus.Finalized, (await _h.Sender.Send(new GetBillQuery(september.Id)))!.Status);
        Assert.Equal(99_000 - 1_000, await _h.StockAsync(rice.Id));   // nothing restored

        Assert.True((await _h.Sender.Send(new VoidBillCommand(october.Id, "returned"))).IsSuccess);   // this month: no question
        Assert.True((await _h.Sender.Send(new VoidBillCommand(september.Id, "returned", ConfirmPastMonth: true))).IsSuccess);
        Assert.Equal(BillStatus.Void, (await _h.Sender.Send(new GetBillQuery(september.Id)))!.Status);
        Assert.False(string.IsNullOrWhiteSpace(Text.Error(new Error(ErrorCodes.BillVoidPastMonth))));
    }

    // ---------- A2-15 customer on the receipt ----------

    [Fact]
    public async Task A2_15_AttachedCustomer_PrintsNameAndAddress_AndShareRendersWithIt()
    {
        var capture = new CapturingRenderer();
        using var h = new AppHost(configure: s => s.AddSingleton<IReceiptRenderer>(capture));
        h.Subscription.IsPro = true;
        await h.SeedShopAsync(Gstin);
        var ravi = (await h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "Ravi Teja", Address = "12 Main Road, Warangal" }))).Value;
        var rice = await h.AddItemAsync("Rice", 6_250);
        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 1_000));
        draft.CustomerId = ravi;
        var done = await h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(done.IsSuccess, done.Error.Code);

        Assert.True((await h.Sender.Send(new ShareReceiptCommand(done.Value.BillId))).IsSuccess);
        Assert.NotNull(capture.Customer);
        Assert.Equal(ravi, capture.Customer.Id);

        var text = h.Services.GetRequiredService<ILocalizer>();
        var bill = (await h.Sender.Send(new GetBillQuery(done.Value.BillId)))!;
        var shop = (await h.Sender.Send(new GetShopProfileQuery()))!;
        var rows = ReceiptLayout.Build(bill, shop, text, capture.Customer);
        Assert.Contains(rows, r => r.Text == text["receipt.customer"] && r.Value == "Ravi Teja");
        Assert.Contains(rows, r => r.Text == "12 Main Road, Warangal");
        Assert.DoesNotContain(ReceiptLayout.Build(bill, shop, text), r => r.Text == text["receipt.customer"]);
    }

    private sealed class CapturingRenderer : IReceiptRenderer
    {
        public Customer? Customer { get; private set; }
        public Task<Result<string>> RenderAsync(Bill bill, ShopProfile shop, CancellationToken ct = default) =>
            RenderAsync(bill, shop, null, ct);
        public Task<Result<string>> RenderAsync(Bill bill, ShopProfile shop, Customer? customer, CancellationToken ct = default)
        {
            Customer = customer;
            return Task.FromResult(Result.Success(Path.Combine(Path.GetTempPath(), bill.Number + ".pdf")));
        }
    }

    // ---------- A2-16 bill prefix ----------

    [Theory]
    [InlineData("A", true)]
    [InlineData("INV/26-", true)]
    [InlineData("ABCD1234", true)]
    [InlineData("ABCDE1234", false)]   // 9: the number would pass 16 characters
    [InlineData("A B", false)]
    [InlineData("A_1", false)]
    [InlineData("బి", false)]
    [InlineData(" ", false)]
    public async Task A2_16_BillPrefix_IsOneToEightLettersDigitsSlashOrDash(string prefix, bool ok)
    {
        var r = await _h.Sender.Send(new SaveShopProfileCommand(new ShopProfile { Name = "Ramu", UpiId = "ramu@okaxis", BillPrefix = prefix }));
        if (ok) Assert.True(r.IsSuccess, r.Error.Code);
        else Assert.Equal(ErrorCodes.BillPrefixInvalid, r.Error.Code);
    }

    [Fact]
    public async Task A2_16_LongestPrefix_GivesANumberWithin16Characters()
    {
        await SeedShopAsync(null, prefix: "INV/2026");
        var rice = await _h.AddItemAsync("Rice", 6_250);
        var bill = await SellAsync(TenderType.Cash, null, (rice, 1_000));
        Assert.Equal("INV/2026-000001", bill.Number);
        Assert.True(bill.Number.Length <= 16);
    }

    // ---------- A1-01 a commit that fails after the handler returned ----------

    [Fact]
    public async Task A1_01_CommitFailsAfterTheHandler_TheSameDraftCanBeRetried()
    {
        var uow = new FailingCommitUnitOfWork();
        using var h = new AppHost(configure: s => s.AddSingleton<IUnitOfWork>(sp => uow.Wrap(sp.GetRequiredService<AngadiDb>())));
        h.Subscription.IsPro = true;
        await h.SeedShopAsync();
        var rice = await h.AddItemAsync("Rice", 6_250, stockMilli: 10_000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1_000));

        uow.FailNextCommit = true;
        var failed = await h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.Equal(ErrorCodes.StorageFull, failed.Error.Code);
        Assert.Null(await h.Sender.Send(new GetBillQuery(draft.Id)));   // rolled back
        Assert.Equal(10_000, await h.StockAsync(rice.Id));

        var retried = await h.Sender.Send(new FinalizeBillCommand(draft));   // the counter's retry: same object
        Assert.True(retried.IsSuccess, retried.Error.Code);
        Assert.Equal("A-000001", retried.Value.Number);                      // the rolled-back number is reused, not skipped
        Assert.Equal(Paise.Zero, retried.Value.ChangeDue);
        Assert.Equal(draft.GrandTotal, draft.CashReceived);                  // exact cash, auto-filled once
        Assert.Equal(9_000, await h.StockAsync(rice.Id));
        Assert.Equal(1, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    private sealed class FailingCommitUnitOfWork
    {
        public bool FailNextCommit { get; set; }

        public IUnitOfWork Wrap(IUnitOfWork inner) => new Wrapper(this, inner);

        private sealed class Wrapper(FailingCommitUnitOfWork owner, IUnitOfWork inner) : IUnitOfWork
        {
            public async Task<T> ExecuteAsync<T>(Func<Task<T>> work, Func<T, bool> shouldCommit, CancellationToken ct = default)
            {
                if (!owner.FailNextCommit) return await inner.ExecuteAsync(work, shouldCommit, ct);
                owner.FailNextCommit = false;
                await inner.ExecuteAsync(work, _ => false, ct);   // the work ran; the commit "fails" → rolled back
                throw new StorageFullException("disk full at commit");
            }
        }
    }

    // ---------- A1-08 quantity limits ----------

    [Fact]
    public void A1_08_SetQty_AboveTheLineLimit_IsRefused_AndTheLineIsUnchanged()
    {
        var draft = new BillDraft(shopHasGstin: true);
        var rice = new Item { TeluguName = "Rice", EnglishName = "Rice", Unit = Unit.Kilogram, Price = new Paise(6_250) };
        var line = draft.Add(rice, Qty.Whole(2));
        var total = draft.Bill.GrandTotal;

        Assert.False(draft.SetQty(line.Id, new Qty(BillDraft.MaxLineQtyMilli + 1)));
        Assert.Equal(2_000, line.Qty.Milli);
        Assert.Equal(total, draft.Bill.GrandTotal);
        Assert.True(draft.SetQty(line.Id, new Qty(BillDraft.MaxLineQtyMilli)));
        Assert.Equal(BillDraft.MaxLineQtyMilli, line.Qty.Milli);
    }

    [Fact]
    public void A1_08_SetQty_ThatOverflowsTheMoneyMaths_RestoresThePreviousQty()
    {
        var draft = new BillDraft(shopHasGstin: true);
        var gold = new Item { TeluguName = "Gold", EnglishName = "Gold", Unit = Unit.Piece, Price = new Paise(1_000_000_000_000_000) };
        var line = draft.Add(gold, Qty.Whole(1));
        var total = draft.Bill.GrandTotal;

        Assert.False(draft.SetQty(line.Id, Qty.Whole(10)));   // 10^15 paise × 10,000 milli overflows long
        Assert.Equal(1_000, line.Qty.Milli);
        Assert.Equal(total, draft.Bill.GrandTotal);           // the screen and the bill agree
    }

    [Fact]
    public async Task A1_08_FinalizeAboveTheLimit_SaysTooLarge_NotMustBeMoreThanZero()
    {
        await SeedShopAsync(null);
        var rice = await _h.AddItemAsync("Rice", 100);
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, BillDraft.MaxLineQtyMilli + 1))));
        Assert.Equal(ErrorCodes.BillQtyTooLarge, r.Error.Code);
        var zero = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 0))));
        Assert.Equal(ErrorCodes.BillQtyInvalid, zero.Error.Code);
    }

    // ---------- A1-06 unique pick labels ----------

    [Fact]
    public void A1_06_TwoCustomersWithTheSameNameAndDues_GetDistinctLabels_ThatMapBackExactly()
    {
        var found = new[]
        {
            new Customer { Name = "Ramesh", Phone = "919876543210" },
            new Customer { Name = "Ramesh", Phone = "919000011111" },
            new Customer { Name = "Ramesh" },
        };
        var labels = ChoiceLabels.Numbered(found.Select(c => $"{c.Name} · {IndianFormat.Rupees(c.DuesBalance)}"
            + (ChoiceLabels.PhoneTail(c.Phone) is { Length: > 0 } tail ? " · " + tail : "")));

        Assert.Equal(3, labels.Distinct(StringComparer.Ordinal).Count());
        for (var i = 0; i < found.Length; i++) Assert.Same(found[i], found[labels.IndexOf(labels[i])]);
        Assert.EndsWith("••3210", labels[0], StringComparison.Ordinal);
        Assert.Equal("", ChoiceLabels.PhoneTail(null));
        Assert.Equal("", ChoiceLabels.PhoneTail("12"));
        Assert.Equal(["1. x", "2. x"], ChoiceLabels.Numbered(["x", "x"]));
    }
}
