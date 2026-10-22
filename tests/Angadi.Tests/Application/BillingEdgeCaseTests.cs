using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Events;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>
/// T1: everything a bill touches, through the real pipeline (validators → transaction → handler → SQLite):
/// tenders, void, the free quota, bill numbering under failure/retry/concurrency, and the reports that add bills up.
/// </summary>
public class BillingEdgeCaseTests : IDisposable
{
    private readonly FailSwitch _fail = new();
    private readonly AppHost _h;

    public BillingEdgeCaseTests()
    {
        // The real outbox, with a switch that makes the LAST write of a finalize (BillFinalized) throw — after the bill
        // number, the bill row, the stock moves and the dues charge have all been written in the transaction.
        _h = new AppHost(configure: s => s.AddSingleton<IOutbox>(sp =>
            new FlakyOutbox(new Outbox(sp.GetRequiredService<AngadiDb>(), sp.GetRequiredService<IClock>()), _fail)));
    }

    public void Dispose() => _h.Dispose();

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    // ---------------------------------------------------------------- helpers

    private async Task<(Item rice, Item soap, Customer ravi)> SetupAsync(string? gstin = null)
    {
        await _h.SeedShopAsync(gstin);
        var rice = await _h.AddItemAsync("బియ్యం", 5_850, GstSlab.Five, stockMilli: 1_000_000);                         // ₹58.50/kg
        var soap = await _h.AddItemAsync("సబ్బు", 4_700, GstSlab.Eighteen, stockMilli: 1_000_000, unit: Unit.Piece);   // ₹47
        var ravi = new Customer { Name = "రవి", Phone = "9876543210" };
        Assert.True((await _h.Sender.Send(new SaveCustomerCommand(ravi))).IsSuccess);
        return (rice, soap, ravi);
    }

    // L-7 (Muse): a ₹0 bill needs the shopkeeper's "give it free?" confirmation; these money tests sell fully discounted
    // bills on purpose, so they confirm like the counter would. The confirmation rule itself is tested in HandlerTests.
    private Task<Result<FinalizedBill>> FinalizeAsync(Bill draft) =>
        _h.Sender.Send(new FinalizeBillCommand(draft) { FreeBillConfirmed = true });

    private async Task<FinalizedBill> SellAsync(Bill draft)
    {
        var r = await FinalizeAsync(draft);
        Assert.True(r.IsSuccess, r.Error.Code + " " + r.Error.Detail);
        return r.Value;
    }

    private static Bill Udhaar(Customer c, params (Item item, long qtyMilli)[] lines)
    {
        var d = AppHost.Draft(TenderType.Udhaar, lines);
        d.CustomerId = c.Id;
        return d;
    }

    private async Task<long> DuesAsync(Guid customerId) => (await _h.Sender.Send(new GetCustomerQuery(customerId)))!.DuesBalance.Value;
    private Task<IReadOnlyList<DuesEntry>> LedgerAsync(Guid customerId) => _h.Sender.Send(new GetCustomerLedgerQuery(customerId));
    private async Task<int> UsedAsync() => (await _h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth;
    private async Task<Bill> BillAsync(Guid id) => (await _h.Sender.Send(new GetBillQuery(id)))!;

    private async Task<IReadOnlyList<StockMove>> MovesAsync(Guid itemId, Guid billId) =>
        (await _h.Services.GetRequiredService<IStockLedger>().GetHistoryAsync(itemId)).Where(m => m.BillId == billId).ToList();

    private async Task<string[]> NumbersAsync() =>
        (await _h.Sender.Send(new ListBillsQuery(new DateOnly(2000, 1, 1), new DateOnly(2100, 1, 1))))
            .Select(b => b.Number).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string[] Expected(int count) => Enumerable.Range(1, count).Select(i => $"A-{i:D6}").ToArray();

    // ================================================================ tenders

    [Fact]
    public async Task Cash_Exact_NothingTyped_RecordsCashEqualToTotal_NoChange()
    {
        var (rice, _, _) = await SetupAsync();
        var r = await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 3_000)));   // 3 × 58.50 = 175.50 → ₹176

        Assert.Equal(17_600, r.GrandTotal.Value);
        Assert.Equal(0, r.ChangeDue.Value);
        var saved = await BillAsync(r.BillId);
        Assert.Equal(17_600, saved.CashReceived.Value);
        Assert.Equal(50, saved.RoundingAdjustment.Value);
        Assert.Null(saved.UpiRef);
    }

    [Fact]
    public async Task Cash_ExactTyped_NoChange_AndOverpaid_ReturnsExactChange()
    {
        var (rice, _, _) = await SetupAsync();
        var exact = AppHost.Draft(TenderType.Cash, (rice, 3_000));
        exact.CashReceived = new Paise(17_600);
        Assert.Equal(0, (await SellAsync(exact)).ChangeDue.Value);

        var over = AppHost.Draft(TenderType.Cash, (rice, 3_000));
        over.CashReceived = Paise.FromRupees(500);
        var r = await SellAsync(over);
        Assert.Equal(32_400, r.ChangeDue.Value);
        Assert.Equal(50_000, (await BillAsync(r.BillId)).CashReceived.Value);   // what was handed over is what is stored
    }

    [Theory]
    [InlineData(15_000, 2_600)]   // ₹150 for a ₹176 bill: ₹26 short
    [InlineData(17_599, 1)]       // one paisa short is still short
    [InlineData(1, 17_599)]
    public async Task Cash_Short_IsRejected_WithTheShortfall_AndNothingIsTaken(long received, long shortBy)
    {
        var (rice, _, _) = await SetupAsync();
        var draft = AppHost.Draft(TenderType.Cash, (rice, 3_000));
        draft.CashReceived = new Paise(received);

        var r = await FinalizeAsync(draft);

        Assert.Equal(ErrorCodes.BillCashShort, r.Error.Code);
        var arg = Assert.Single(r.Error.Args!);
        Assert.Equal(IndianFormat.Rupees(new Paise(shortBy)), arg);
        Assert.Equal(BillStatus.Draft, draft.Status);
        Assert.Equal("", draft.Number);
        Assert.Equal(received, draft.CashReceived.Value);            // the typed amount is left for the shopkeeper to fix
        Assert.Equal(1_000_000, await _h.StockAsync(rice.Id));
        Assert.Equal(0, await UsedAsync());
        Assert.Equal("A-000001", (await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)))).Number);   // no number burnt
    }

    [Fact]
    public async Task Upi_WithReference_IsStoredTrimmed_WithoutReference_IsNull_NeverChange()
    {
        var (rice, _, _) = await SetupAsync();
        var withRef = AppHost.Draft(TenderType.Upi, (rice, 2_000));
        withRef.UpiRef = "  412398765432 ";
        var a = await SellAsync(withRef);
        var b = await SellAsync(AppHost.Draft(TenderType.Upi, (rice, 2_000)));
        var blank = AppHost.Draft(TenderType.Upi, (rice, 2_000));
        blank.UpiRef = "   ";
        var c = await SellAsync(blank);

        Assert.Equal("412398765432", (await BillAsync(a.BillId)).UpiRef);
        Assert.Null((await BillAsync(b.BillId)).UpiRef);
        Assert.Null((await BillAsync(c.BillId)).UpiRef);
        foreach (var r in new[] { a, b, c })
        {
            Assert.Equal(0, r.ChangeDue.Value);
            Assert.Equal(0, (await BillAsync(r.BillId)).CashReceived.Value);
        }
    }

    [Theory]
    [InlineData(TenderType.Upi)]
    [InlineData(TenderType.Udhaar)]
    public async Task CashReceived_OnANonCashTender_IsRejected_AndNothingSaved(TenderType tender)
    {
        var (rice, _, ravi) = await SetupAsync();
        var draft = AppHost.Draft(tender, (rice, 1_000));
        draft.CustomerId = ravi.Id;
        draft.CashReceived = Paise.FromRupees(100);

        Assert.Equal(ErrorCodes.ValidationFailed, (await FinalizeAsync(draft)).Error.Code);
        Assert.Equal(1_000_000, await _h.StockAsync(rice.Id));
        Assert.Equal(0, await DuesAsync(ravi.Id));
    }

    [Theory]
    [InlineData(TenderType.Cash)]
    [InlineData(TenderType.Udhaar)]
    public async Task UpiReference_OnANonUpiTender_IsRejected(TenderType tender)
    {
        var (rice, _, ravi) = await SetupAsync();
        var draft = AppHost.Draft(tender, (rice, 1_000));
        draft.CustomerId = ravi.Id;
        draft.UpiRef = "1234";
        Assert.Equal(ErrorCodes.ValidationFailed, (await FinalizeAsync(draft)).Error.Code);
    }

    [Fact]
    public async Task MissingOrInventedTender_IsRejected()
    {
        var (rice, _, _) = await SetupAsync();
        var none = AppHost.Draft(TenderType.Cash, (rice, 1_000));
        none.Tender = null;
        var invented = AppHost.Draft((TenderType)7, (rice, 1_000));
        Assert.Equal(ErrorCodes.BillTenderMissing, (await FinalizeAsync(none)).Error.Code);
        Assert.Equal(ErrorCodes.BillTenderMissing, (await FinalizeAsync(invented)).Error.Code);
    }

    [Fact]
    public async Task Udhaar_ChargesTheRoundedTotal_ExactlyOnce_EvenWhenRetried()
    {
        var (rice, _, ravi) = await SetupAsync();
        var draft = Udhaar(ravi, (rice, 3_000));   // ₹175.50 → ₹176

        var first = await SellAsync(draft);
        var again = await SellAsync(draft);                                  // double tap
        var copy = Udhaar(ravi, (rice, 3_000));
        copy.Id = draft.Id;
        var restored = await SellAsync(copy);                                // restored draft, same bill id

        Assert.Equal(first.BillId, again.BillId);
        Assert.Equal(first.BillId, restored.BillId);
        Assert.Equal(0, first.ChangeDue.Value);
        Assert.Equal(17_600, await DuesAsync(ravi.Id));
        var charge = Assert.Single(await LedgerAsync(ravi.Id));
        Assert.Equal((DuesEntryKind.Charge, 17_600L, (Guid?)first.BillId), (charge.Kind, charge.Amount.Value, charge.BillId));
        Assert.Equal(997_000, await _h.StockAsync(rice.Id));
        Assert.Equal(1, await UsedAsync());
    }

    [Fact]
    public async Task Udhaar_UnknownCustomer_IsRejected_AndNothingIsTaken()
    {
        var (rice, _, _) = await SetupAsync();
        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 1_000));
        draft.CustomerId = Guid.NewGuid();
        Assert.Equal(ErrorCodes.CustomerNotFound, (await FinalizeAsync(draft)).Error.Code);
        Assert.Equal(1_000_000, await _h.StockAsync(rice.Id));
        Assert.Equal(0, await UsedAsync());
    }

    [Fact]
    public async Task Udhaar_FullyDiscountedToZero_FinalizesWithoutADuesEntry()
    {
        var (rice, _, ravi) = await SetupAsync();
        var draft = Udhaar(ravi, (rice, 1_000));
        draft.BillDiscount = Discount.Percent(10_000);
        var r = await SellAsync(draft);
        Assert.Equal(0, r.GrandTotal.Value);
        Assert.Empty(await LedgerAsync(ravi.Id));
        Assert.Equal(0, await DuesAsync(ravi.Id));
        Assert.True((await _h.Sender.Send(new VoidBillCommand(r.BillId, "test"))).IsSuccess);   // and it voids cleanly
        Assert.Empty(await LedgerAsync(ravi.Id));
    }

    [Theory]
    [InlineData(TenderType.Cash)]
    [InlineData(TenderType.Upi)]
    public async Task CustomerAttached_ToAPaidBill_KeepsTheLink_ButNeverTouchesDues(TenderType tender)
    {
        var (rice, _, ravi) = await SetupAsync();
        var draft = AppHost.Draft(tender, (rice, 2_000));
        draft.CustomerId = ravi.Id;
        var r = await SellAsync(draft);

        Assert.Equal(ravi.Id, (await BillAsync(r.BillId)).CustomerId);
        Assert.Equal(0, await DuesAsync(ravi.Id));
        Assert.Empty(await LedgerAsync(ravi.Id));

        Assert.True((await _h.Sender.Send(new VoidBillCommand(r.BillId, "తప్పు"))).IsSuccess);
        Assert.Equal(0, await DuesAsync(ravi.Id));                       // void of a paid bill reverses nothing
        Assert.Empty(await LedgerAsync(ravi.Id));
    }

    // ================================================================ void

    [Fact]
    public async Task Void_ReturnsFractionalStockExactly_OneMovePerItem_AcrossSplitLines()
    {
        var (rice, soap, _) = await SetupAsync();
        var draft = AppHost.Draft(TenderType.Cash, (rice, 250), (soap, 3_000), (rice, 1_500));   // rice on two lines: ¼ kg + 1½ kg
        var r = await SellAsync(draft);
        Assert.Equal(1_000_000 - 1_750, await _h.StockAsync(rice.Id));

        Assert.True((await _h.Sender.Send(new VoidBillCommand(r.BillId, "కస్టమర్ వెనక్కి ఇచ్చారు"))).IsSuccess);

        Assert.Equal(1_000_000, await _h.StockAsync(rice.Id));
        Assert.Equal(1_000_000, await _h.StockAsync(soap.Id));
        var moves = await MovesAsync(rice.Id, r.BillId);
        Assert.Equal(new[] { (StockMoveReason.Sale, -1_750L), (StockMoveReason.VoidRestore, 1_750L) },
            moves.Select(m => (m.Reason, m.Delta.Milli)).OrderBy(m => m.Reason));
        var voided = await BillAsync(r.BillId);
        Assert.Equal(BillStatus.Void, voided.Status);
        Assert.Equal("కస్టమర్ వెనక్కి ఇచ్చారు", voided.VoidReason);
        Assert.Equal(r.Number, voided.Number);   // the number stays with the voided bill, never reused
    }

    [Fact]
    public async Task Void_Udhaar_ReversesExactlyTheCharge_AndASecondVoidChangesNothing()
    {
        var (rice, _, ravi) = await SetupAsync();
        await SellAsync(Udhaar(ravi, (rice, 1_000)));                    // ₹58.50 → ₹59, stays owed
        var r = await SellAsync(Udhaar(ravi, (rice, 3_000)));            // ₹176
        Assert.Equal(23_500, await DuesAsync(ravi.Id));

        Assert.True((await _h.Sender.Send(new VoidBillCommand(r.BillId, "తప్పు"))).IsSuccess);
        var second = await _h.Sender.Send(new VoidBillCommand(r.BillId, "మళ్ళీ"));

        Assert.Equal(ErrorCodes.BillAlreadyVoid, second.Error.Code);
        Assert.Equal(5_900, await DuesAsync(ravi.Id));
        var reversal = Assert.Single(await LedgerAsync(ravi.Id), e => e.Kind == DuesEntryKind.Reversal);
        Assert.Equal((17_600L, (Guid?)r.BillId), (reversal.Amount.Value, reversal.BillId));
        Assert.Equal(1_000_000 - 1_000, await _h.StockAsync(rice.Id));  // restored once only
    }

    [Fact]
    public async Task Void_AfterTheBillWasPaidInFull_IsBlocked_AndNothingChanges()
    {
        var (rice, _, ravi) = await SetupAsync();
        var r = await SellAsync(Udhaar(ravi, (rice, 2_000)));            // ₹117
        Assert.True((await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, new Paise(11_700), "paid"))).IsSuccess);

        var blocked = await _h.Sender.Send(new VoidBillCommand(r.BillId, "తప్పు"));

        Assert.Equal(ErrorCodes.BillVoidBlockedByPayments, blocked.Error.Code);
        Assert.Equal(0, await DuesAsync(ravi.Id));                      // never negative
        Assert.Equal(BillStatus.Finalized, (await BillAsync(r.BillId)).Status);
        Assert.Equal(1_000_000 - 2_000, await _h.StockAsync(rice.Id));
    }

    [Fact]
    public async Task Void_PartlyPaidCustomer_NeverLeavesANegativeBalance()
    {
        var (rice, _, ravi) = await SetupAsync();
        var a = await SellAsync(Udhaar(ravi, (rice, 2_000)));            // ₹117
        var b = await SellAsync(Udhaar(ravi, (rice, 1_000)));            // ₹59
        Assert.True((await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, new Paise(10_000), null))).IsSuccess);   // owes ₹76

        var voidA = await _h.Sender.Send(new VoidBillCommand(a.BillId, "x"));   // ₹117 > ₹76 owed: blocked
        Assert.Equal(ErrorCodes.BillVoidBlockedByPayments, voidA.Error.Code);
        Assert.True((await _h.Sender.Send(new VoidBillCommand(b.BillId, "x"))).IsSuccess);   // ₹59 ≤ ₹76
        Assert.Equal(1_700, await DuesAsync(ravi.Id));
    }

    [Fact]
    public async Task Void_UnknownBill_IsNotFound()
    {
        await SetupAsync();
        Assert.Equal(ErrorCodes.BillNotFound, (await _h.Sender.Send(new VoidBillCommand(Guid.NewGuid(), "x"))).Error.Code);
    }

    [Fact]
    public async Task VoidedBills_StillCountForTheFreeQuota()
    {
        var (rice, _, _) = await SetupAsync();
        var bills = new List<FinalizedBill>();
        for (var i = 0; i < 50; i++) bills.Add(await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000))));
        foreach (var b in bills.Take(10)) Assert.True((await _h.Sender.Send(new VoidBillCommand(b.BillId, "తప్పు"))).IsSuccess);

        Assert.Equal(50, await UsedAsync());
        Assert.Equal(ErrorCodes.FreeLimitReached, (await FinalizeAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)))).Error.Code);
    }

    [Fact]
    public async Task Reports_LeaveVoidedBillsOut_OfEveryMoneyTotal()
    {
        var (rice, soap, ravi) = await SetupAsync(gstin: "36ABCDE1234F1Z5");
        var cash = AppHost.Draft(TenderType.Cash, (rice, 2_000), (soap, 1_000));   // 117 + 47 = ₹164
        cash.BillDiscount = Discount.Flat(Paise.FromRupees(4));                   // ₹160
        var keptCash = await SellAsync(cash);
        var keptUpi = await SellAsync(AppHost.Draft(TenderType.Upi, (soap, 2_000)));        // ₹94
        var keptUdhaar = await SellAsync(Udhaar(ravi, (rice, 1_000)));                      // ₹59
        var gone = AppHost.Draft(TenderType.Upi, (soap, 10_000), (rice, 5_000));            // ₹470 + ₹292.50
        gone.BillDiscount = Discount.Percent(1_000);
        var voided = await SellAsync(gone);
        Assert.True((await _h.Sender.Send(new VoidBillCommand(voided.BillId, "తప్పు"))).IsSuccess);

        var day = _h.Clock.Today;
        var report = await _h.Sender.Send(new GetDayEndReportQuery(day));
        var kept = new[] { await BillAsync(keptCash.BillId), await BillAsync(keptUpi.BillId), await BillAsync(keptUdhaar.BillId) };

        Assert.Equal(3, report.BillCount);
        Assert.Equal(1, report.VoidCount);
        Assert.Equal(16_000 + 9_400 + 5_900, report.SalesTotal.Value);
        Assert.Equal((16_000L, 9_400L, 5_900L), (report.Cash.Value, report.Upi.Value, report.Udhaar.Value));
        Assert.Equal(report.SalesTotal.Value, report.Cash.Value + report.Upi.Value + report.Udhaar.Value);
        Assert.Equal(400, report.Discounts.Value);
        Assert.Equal(kept.Sum(b => b.TaxTotal.Value), report.Tax.Value);

        var gst = await _h.Sender.Send(new GetGstSummaryQuery(day, day));
        Assert.Equal(report.Tax.Value, gst.Sum(s => s.Tax.Value));
        Assert.Equal(kept.Sum(b => b.SubTotal.Value - b.BillDiscountAmount.Value), gst.Sum(s => s.Taxable.Value + s.Tax.Value));

        var top = await _h.Sender.Send(new GetTopItemsQuery(day, day));
        Assert.Equal(3_000, top.Single(t => t.ItemId == rice.Id).QtySold.Milli);   // the voided 5 kg is not "sold"
        Assert.Equal(3_000, top.Single(t => t.ItemId == soap.Id).QtySold.Milli);
        Assert.Equal(16_000 + 9_400 + 5_900, top.Sum(t => t.Value.Value) + kept.Sum(b => b.RoundingAdjustment.Value));
    }

    // ================================================================ free quota

    [Fact]
    public async Task FreeQuota_ExactlyFifty_ThenFreeLimitReached_WithLimitAndPrice_AndNothingTaken()
    {
        var (rice, _, _) = await SetupAsync();
        for (var i = 0; i < 49; i++) await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));
        var quota = await _h.Sender.Send(new GetBillQuotaQuery());
        Assert.Equal((49, true), (quota.UsedThisMonth, quota.CanFinalize));
        await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));                    // the 50th is free
        Assert.False((await _h.Sender.Send(new GetBillQuotaQuery())).CanFinalize);

        var blocked = await FinalizeAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));

        Assert.Equal(ErrorCodes.FreeLimitReached, blocked.Error.Code);
        Assert.Equal(new object[] { 50, 200 }, blocked.Error.Args);
        Assert.Equal(1_000_000 - 50_000, await _h.StockAsync(rice.Id));
        Assert.Equal(50, await UsedAsync());
        Assert.Equal(Expected(50), await NumbersAsync());
    }

    [Fact]
    public async Task FreeQuota_RetryOfTheFiftiethBill_AtTheLimit_ReturnsThatBill_NotTheLimit()
    {
        var (rice, _, ravi) = await SetupAsync();
        for (var i = 0; i < 49; i++) await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));
        var fiftieth = Udhaar(ravi, (rice, 1_000));
        var first = await SellAsync(fiftieth);

        var retry = await FinalizeAsync(fiftieth);

        Assert.True(retry.IsSuccess, retry.Error.Code);
        Assert.Equal((first.BillId, first.Number), (retry.Value.BillId, retry.Value.Number));
        Assert.Equal(50, await UsedAsync());
        Assert.Equal(5_900, await DuesAsync(ravi.Id));
    }

    [Fact]
    public async Task Pro_IsNeverLimited()
    {
        var (rice, _, _) = await SetupAsync();
        _h.Subscription.IsPro = true;
        for (var i = 0; i < 60; i++) await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));
        var quota = await _h.Sender.Send(new GetBillQuotaQuery());
        Assert.True(quota.CanFinalize);
        Assert.True(quota.IsPro);
        Assert.Equal(Expected(60), await NumbersAsync());
    }

    /// <summary>
    /// For Pro, finalize skips the quota read (short-circuit), so the month's counter is first seeded inside
    /// IncrementAsync — after the bill row is saved — and the first bill of the month is counted twice.
    /// </summary>
    [Fact(Skip = "BUG T1-03: a Pro shop's first bill of each month is counted twice by the quota (seed runs after the bill is saved)")]
    public async Task Pro_BillsAreCountedExactlyOnce()
    {
        var (rice, _, _) = await SetupAsync();
        _h.Subscription.IsPro = true;
        for (var i = 0; i < 3; i++) await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));
        Assert.Equal(3, await UsedAsync());
        _h.Subscription.IsPro = false;   // subscription lapses: 47 free bills are left, not 46
        Assert.Equal(47, (await _h.Sender.Send(new GetBillQuotaQuery())).FreeLimit - await UsedAsync());
    }

    [Fact]
    public async Task FreeQuota_ResetsAtMidnightIst_OnTheFirst_NotAtUtcMidnight()
    {
        var (rice, _, _) = await SetupAsync();
        _h.Clock.Now = new DateTimeOffset(2026, 10, 31, 23, 50, 0, Ist);
        for (var i = 0; i < 50; i++) await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));

        _h.Clock.Now = new DateTimeOffset(2026, 10, 31, 23, 59, 59, Ist);
        Assert.Equal(ErrorCodes.FreeLimitReached, (await FinalizeAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)))).Error.Code);

        // 00:00 IST on 1 Nov is still 31 Oct 18:30 UTC: the shop's month has turned, so the free bills are back.
        _h.Clock.Now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, Ist);
        Assert.Equal(0, await UsedAsync());
        var nov = await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));
        Assert.Equal(1, await UsedAsync());
        Assert.Equal("A-000051", nov.Number);   // numbering runs on across months
        Assert.Equal(new DateOnly(2026, 11, 1), DateOnly.FromDateTime((await BillAsync(nov.BillId)).FinalizedAt!.Value.DateTime));

        var oct = await _h.Sender.Send(new GetDayEndReportQuery(new DateOnly(2026, 10, 31)));
        Assert.Equal(50, oct.BillCount);
    }

    // ================================================================ numbering

    [Fact]
    public async Task Numbering_FailureAfterTheNumberIsTaken_RollsBackEverything_AndTheRetryGetsTheSameNumber()
    {
        var (rice, _, ravi) = await SetupAsync();
        var draft = Udhaar(ravi, (rice, 2_000));
        _fail.FailuresLeft = 1;

        var failed = await FinalizeAsync(draft);

        Assert.True(failed.IsFailure);
        Assert.Equal(0, _fail.FailuresLeft);                      // it really failed at the very end
        Assert.Equal((BillStatus.Draft, ""), (draft.Status, draft.Number));
        Assert.Null(draft.FinalizedAt);
        Assert.Null(await _h.Sender.Send(new GetBillQuery(draft.Id)));
        Assert.Equal(1_000_000, await _h.StockAsync(rice.Id));
        Assert.Equal(0, await DuesAsync(ravi.Id));
        Assert.Empty(await LedgerAsync(ravi.Id));
        Assert.Equal(0, await UsedAsync());

        var retried = await SellAsync(draft);
        var next = await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));
        Assert.Equal(("A-000001", "A-000002"), (retried.Number, next.Number));
        Assert.Equal(11_700, await DuesAsync(ravi.Id));
        Assert.Equal(2, await UsedAsync());
    }

    [Fact]
    public async Task Numbering_ExactCashAutoFill_DoesNotSurviveARolledBackFinalize()
    {
        var (rice, _, _) = await SetupAsync();
        var draft = AppHost.Draft(TenderType.Cash, (rice, 2_000));
        _fail.FailuresLeft = 1;
        Assert.True((await FinalizeAsync(draft)).IsFailure);
        Assert.Equal(0, draft.CashReceived.Value);

        draft.Lines.Add(AppHost.Draft(TenderType.Cash, (rice, 1_000)).Lines[0]);   // the shopkeeper adds a kilo and pays again
        var r = await SellAsync(draft);
        Assert.Equal(17_600, r.GrandTotal.Value);
        Assert.Equal(17_600, (await BillAsync(r.BillId)).CashReceived.Value);       // not the stale ₹117
    }

    [Fact]
    public async Task Numbering_RejectedFinalizes_NeverBurnANumber()
    {
        var (rice, _, _) = await SetupAsync();
        await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));                                  // A-000001

        var short_ = AppHost.Draft(TenderType.Cash, (rice, 1_000)); short_.CashReceived = new Paise(100);
        var stranger = AppHost.Draft(TenderType.Upi, (rice, 1_000)); stranger.CustomerId = Guid.NewGuid();
        var ghostItem = AppHost.Draft(TenderType.Cash, (new Item { Id = Guid.NewGuid(), Price = new Paise(100) }, 1_000));
        var huge = AppHost.Draft(TenderType.Cash, (rice, 1_000)); huge.Lines[0].UnitPrice = new Paise(long.MaxValue / 100);
        var tooMany = AppHost.Draft(TenderType.Cash, (rice, FinalizeBillValidator.MaxLineQtyMilli + 1));
        var empty = AppHost.Draft(TenderType.Cash);

        Assert.Equal(ErrorCodes.BillCashShort, (await FinalizeAsync(short_)).Error.Code);
        Assert.Equal(ErrorCodes.CustomerNotFound, (await FinalizeAsync(stranger)).Error.Code);
        Assert.Equal(ErrorCodes.ItemNotFound, (await FinalizeAsync(ghostItem)).Error.Code);
        Assert.Equal(ErrorCodes.BillAmountTooLarge, (await FinalizeAsync(huge)).Error.Code);
        Assert.Equal(ErrorCodes.BillQtyInvalid, (await FinalizeAsync(tooMany)).Error.Code);
        Assert.Equal(ErrorCodes.BillEmpty, (await FinalizeAsync(empty)).Error.Code);
        _fail.FailuresLeft = 1;
        Assert.True((await FinalizeAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)))).IsFailure);   // thrown + rolled back

        await SellAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)));                                  // A-000002
        Assert.Equal(Expected(2), await NumbersAsync());
        Assert.Equal(1_000_000 - 2_000, await _h.StockAsync(rice.Id));
        Assert.Equal(2, await UsedAsync());
    }

    [Fact]
    public async Task Numbering_LineQuantityCap_IsInclusive()
    {
        var (_, soap, _) = await SetupAsync();
        var atCap = AppHost.Draft(TenderType.Upi, (soap, FinalizeBillValidator.MaxLineQtyMilli));   // 1,00,000 soaps
        var r = await SellAsync(atCap);
        Assert.Equal(4_700L * 100_000, r.GrandTotal.Value);
    }

    [Fact]
    public async Task Numbering_ConcurrentFinalizes_GetUniqueGaplessNumbers()
    {
        var (rice, _, _) = await SetupAsync();
        _h.Subscription.IsPro = true;
        var results = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => FinalizeAsync(AppHost.Draft(TenderType.Cash, (rice, 1_000)))));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error.Code));
        Assert.Equal(Expected(25), results.Select(r => r.Value.Number).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(1_000_000 - 25_000, await _h.StockAsync(rice.Id));   // quota count under Pro: see T1-03
    }

    [Fact]
    public async Task Numbering_DoubleSubmitOfOneBill_InParallel_MakesOneBill()
    {
        var (rice, _, ravi) = await SetupAsync();
        var a = Udhaar(ravi, (rice, 2_000));
        var b = Udhaar(ravi, (rice, 2_000));
        b.Id = a.Id;

        var both = await Task.WhenAll(FinalizeAsync(a), FinalizeAsync(b), FinalizeAsync(a));

        Assert.All(both, r => Assert.True(r.IsSuccess, r.Error.Code));
        Assert.Single(both.Select(r => r.Value.Number).Distinct());
        Assert.Equal(Expected(1), await NumbersAsync());
        Assert.Equal(11_700, await DuesAsync(ravi.Id));
        Assert.Equal(1_000_000 - 2_000, await _h.StockAsync(rice.Id));
        Assert.Equal(1, await UsedAsync());
    }

    /// <summary>
    /// The counter's 45 s watchdog tells the shopkeeper "paying again is safe" while the first save may already have
    /// committed. If they add the forgotten item and pay again, the retry must not report success for the OLD bill.
    /// </summary>
    [Fact(Skip = "BUG T1-01: an idempotent retry of an EDITED bill returns the old saved bill as success; the added item is never billed")]
    public async Task Retry_OfABillEditedAfterItSaved_IsNotReportedAsPaidAtTheOldTotal()
    {
        var (rice, soap, _) = await SetupAsync();
        var draft = new BillDraft(shopHasGstin: false);
        draft.Add(rice, Qty.Whole(2));
        draft.SetTender(TenderType.Upi);
        await SellAsync(draft.Bill);                         // committed, but the UI timed out and kept the bill on screen

        draft.Add(soap);                                     // forgotten soap: ₹47 more on screen and on the UPI QR
        var retry = await FinalizeAsync(draft.Bill);

        // Either refuse (the bill already saved — start a new one for the soap) or bill what is on screen; never
        // "paid" for a total the shopkeeper is not looking at.
        if (retry.IsSuccess)
        {
            Assert.Equal(draft.Bill.GrandTotal, retry.Value.GrandTotal);
            Assert.Equal(draft.Bill.GrandTotal, (await BillAsync(retry.Value.BillId)).GrandTotal);
            Assert.Equal(1_000_000 - 1_000, await _h.StockAsync(soap.Id));
        }
    }

    // ================================================================ end-to-end money property

    /// <summary>
    /// 60 random counter bills through the real pipeline (fractional qty, discounts, every tender, some voided):
    /// what the draft showed is what was saved, numbers are gapless, and stock, dues and every report add up.
    /// </summary>
    [Fact]
    public async Task RandomDay_EveryLedgerAndReportAddsUp()
    {
        var (rice, soap, ravi) = await SetupAsync(gstin: "36ABCDE1234F1Z5");
        var milk = await _h.AddItemAsync("పాలు", 6_400, GstSlab.Zero, stockMilli: 1_000_000, unit: Unit.Litre);
        var cola = await _h.AddItemAsync("కూల్ డ్రింక్", 4_000, GstSlab.Forty, stockMilli: 1_000_000, unit: Unit.Piece);
        Item[] items = [rice, soap, milk, cola];
        _h.Subscription.IsPro = true;
        var rng = new Random(2026_10_19);

        var sold = new Dictionary<Guid, long>();
        long dues = 0;
        var finals = new List<(Bill Saved, bool Voided)>();
        for (var n = 0; n < 60; n++)
        {
            var picks = Enumerable.Range(0, rng.Next(1, 6)).Select(_ =>
            {
                var item = items[rng.Next(items.Length)];
                var qty = item.Unit is Unit.Piece ? rng.Next(1, 6) * 1_000L : rng.Next(1, 13) * 250L;
                return (item, qty);
            }).ToArray();
            var tender = (TenderType)rng.Next(3);
            var draft = AppHost.Draft(tender, picks);
            if (rng.Next(3) == 0) draft.Lines[0].LineDiscount = Discount.Percent(rng.Next(0, 3_000));
            draft.BillDiscount = rng.Next(3) == 0 ? Discount.Flat(new Paise(rng.Next(0, 2_000))) : Discount.None;
            if (tender == TenderType.Udhaar) draft.CustomerId = ravi.Id;

            // What the counter shows before paying: the same lines through the same calculator.
            var preview = AppHost.Draft(tender, picks);
            preview.Lines[0].LineDiscount = draft.Lines[0].LineDiscount;
            preview.BillDiscount = draft.BillDiscount;
            BillCalculator.Compute(preview, shopHasGstin: true);
            if (tender == TenderType.Cash && rng.Next(2) == 0) draft.CashReceived = preview.GrandTotal + new Paise(rng.Next(0, 50_000));

            var r = await SellAsync(draft);
            Assert.Equal(preview.GrandTotal, r.GrandTotal);
            var saved = await BillAsync(r.BillId);
            Assert.Equal((preview.SubTotal, preview.BillDiscountAmount, preview.TaxTotal, preview.RoundingAdjustment),
                (saved.SubTotal, saved.BillDiscountAmount, saved.TaxTotal, saved.RoundingAdjustment));
            Assert.Equal(preview.TaxSplits, saved.TaxSplits);
            Assert.Equal(preview.Lines.Select(l => (l.Gross, l.LineTotal, l.BillDiscountShare)), saved.Lines.Select(l => (l.Gross, l.LineTotal, l.BillDiscountShare)));
            if (tender == TenderType.Cash) Assert.Equal(BillCalculator.ChangeDue(saved), r.ChangeDue);

            var voidIt = rng.Next(4) == 0;
            if (voidIt) Assert.True((await _h.Sender.Send(new VoidBillCommand(r.BillId, "test"))).IsSuccess);
            else
            {
                foreach (var (item, qty) in picks) sold[item.Id] = sold.GetValueOrDefault(item.Id) + qty;
                if (tender == TenderType.Udhaar) dues += r.GrandTotal.Value;
            }
            finals.Add((saved, voidIt));
        }

        foreach (var item in items) Assert.Equal(1_000_000 - sold.GetValueOrDefault(item.Id), await _h.StockAsync(item.Id));
        Assert.Equal(dues, await DuesAsync(ravi.Id));
        Assert.Equal(Expected(60), await NumbersAsync());

        var kept = finals.Where(f => !f.Voided).Select(f => f.Saved).ToList();
        var day = _h.Clock.Today;
        var report = await _h.Sender.Send(new GetDayEndReportQuery(day));
        Assert.Equal((kept.Count, 60 - kept.Count), (report.BillCount, report.VoidCount));
        Assert.Equal(kept.Sum(b => b.GrandTotal.Value), report.SalesTotal.Value);
        Assert.Equal(report.SalesTotal.Value, report.Cash.Value + report.Upi.Value + report.Udhaar.Value);
        Assert.Equal(dues, report.Udhaar.Value);
        Assert.Equal(kept.Sum(b => b.TaxTotal.Value), report.Tax.Value);
        Assert.Equal(kept.Sum(b => BillCalculator.TotalDiscount(b).Value), report.Discounts.Value);
        var gst = await _h.Sender.Send(new GetGstSummaryQuery(day, day));
        Assert.Equal(report.Tax.Value, gst.Sum(s => s.Tax.Value));
        Assert.Equal(report.SalesTotal.Value - kept.Sum(b => b.RoundingAdjustment.Value), gst.Sum(s => s.Taxable.Value + s.Tax.Value));
    }
}

/// <summary>Arms <see cref="FlakyOutbox"/>: the next N BillFinalized writes throw.</summary>
public sealed class FailSwitch
{
    public int FailuresLeft { get; set; }
}

/// <summary>Real outbox that can be made to fail the last write of a finalize, to prove the whole bill rolls back.</summary>
internal sealed class FlakyOutbox(IOutbox inner, FailSwitch fail) : IOutbox
{
    public Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) => inner.PruneOlderThanAsync(cutoff, ct);

    public Task AppendAsync(string aggregateType, IDomainEvent @event, CancellationToken ct = default)
    {
        if (@event is BillFinalized && fail.FailuresLeft > 0)
        {
            fail.FailuresLeft--;
            throw new InvalidOperationException("simulated failure after the bill was written");
        }
        return inner.AppendAsync(aggregateType, @event, ct);
    }
}
