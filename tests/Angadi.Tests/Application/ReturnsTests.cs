using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Inventory;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Receipts;

namespace Angadi.Tests.Application;

/// <summary>v2 returns / partial refunds (docs/BILLING_RULES.md §Returns), through the real handler graph.</summary>
public sealed class ReturnsTests : IDisposable
{
    private const string Gstin = "36ABCDE1234F1Z5";
    private readonly AppHost _h = new();
    public ReturnsTests() => _h.Subscription.IsPro = true;
    public void Dispose() => _h.Dispose();

    private async Task<Guid> CustomerAsync() => (await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "రవి" }))).Value;

    private async Task<Bill> SellAsync(TenderType tender, Guid? customer, Discount billDiscount, params (Item item, long qtyMilli)[] lines)
    {
        var draft = AppHost.Draft(tender, lines);
        draft.CustomerId = customer;
        draft.BillDiscount = billDiscount;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code : "");
        return (await _h.Sender.Send(new GetBillQuery(r.Value.BillId)))!;
    }

    private async Task<Result<RecordedReturn>> ReturnAsync(Bill bill, RefundMethod method, params (int line, long qtyMilli)[] lines) =>
        await _h.Sender.Send(new RecordReturnCommand(Guid.NewGuid(), bill.Id,
            lines.Select(l => new ReturnRequestLine(bill.Lines[l.line].Id, new Qty(l.qtyMilli))).ToList(), method));

    private async Task<long> DuesAsync(Guid customer) => (await _h.Sender.Send(new GetCustomerQuery(customer)))!.DuesBalance.Value;

    private async Task<long> LedgerDuesAsync(Guid customer) => (await _h.Sender.Send(new GetCustomerLedgerQuery(customer)))
        .Sum(e => e.Kind == DuesEntryKind.Charge ? e.Amount.Value : -e.Amount.Value);

    [Fact]
    public async Task ReturningAWholeBillInParts_RefundsExactlyTheBill_AndReversesExactlyItsGst()
    {
        await _h.SeedShopAsync(Gstin);
        // Awkward prices, quantities and a bill discount so every partial value has rounding in it.
        var rice = await _h.AddItemAsync("బియ్యం", 6_333, GstSlab.Five, 100_000);
        var oil = await _h.AddItemAsync("నూనె", 19_917, GstSlab.Eighteen, 100_000, Unit.Litre);
        var soap = await _h.AddItemAsync("సబ్బు", 3_499, GstSlab.Eighteen, 100_000, Unit.Piece);
        var bill = await SellAsync(TenderType.Cash, null, Discount.Percent(750), (rice, 2_750), (oil, 3_000), (soap, 7_000));

        var refunds = new List<RecordedReturn>
        {
            (await ReturnAsync(bill, RefundMethod.Cash, (0, 1_000), (2, 3_000))).Value,
            (await ReturnAsync(bill, RefundMethod.Upi, (1, 1_000), (0, 250))).Value,
            (await ReturnAsync(bill, RefundMethod.Cash, (2, 1_000))).Value,
            (await ReturnAsync(bill, RefundMethod.Cash, (0, 1_500), (1, 2_000), (2, 3_000))).Value,
        };
        Assert.Equal(bill.GrandTotal.Value, refunds.Sum(r => r.RefundTotal.Value));
        Assert.All(refunds, r => Assert.Equal(0, r.RefundTotal.Value % 100));   // whole rupees, like the bill

        var state = (await _h.Sender.Send(new GetBillReturnsQuery(bill.Id)))!;
        Assert.False(state.CanReturn);
        Assert.Equal(bill.Lines.Sum(l => l.Net.Value), state.Returns.Sum(r => r.LinesTotal.Value));
        foreach (var slab in bill.TaxSplits)
        {
            var back = state.Returns.SelectMany(r => r.TaxSplits).Where(s => s.Slab == slab.Slab).ToList();
            Assert.Equal(slab.Tax.Value, back.Sum(s => s.Tax.Value));
            Assert.Equal(slab.Taxable.Value, back.Sum(s => s.Taxable.Value));
        }

        // GST summary for the period nets to zero; the day-end shows sales, returns and net sales.
        var gst = await _h.Sender.Send(new GetGstSummaryQuery(_h.Clock.Today, _h.Clock.Today));
        Assert.All(gst, s => Assert.Equal(0, s.Tax.Value));
        Assert.All(gst, s => Assert.Equal(0, s.Taxable.Value));
        var day = await _h.Sender.Send(new GetDayEndReportQuery(_h.Clock.Today));
        Assert.Equal(4, day.ReturnCount);
        Assert.Equal(bill.GrandTotal.Value, day.Returns.Value);
        Assert.Equal(0, day.NetSales.Value);
        Assert.Equal(day.Returns.Value, day.CashRefunds.Value + day.UpiRefunds.Value + day.DuesCredited.Value);
        Assert.Equal(refunds[1].RefundTotal.Value, day.UpiRefunds.Value);

        // Everything came back to stock through the ledger.
        Assert.Equal(100_000, await _h.StockAsync(rice.Id));
        Assert.Equal(100_000, await _h.StockAsync(oil.Id));
        Assert.Equal(100_000, await _h.StockAsync(soap.Id));
        Assert.True((await _h.Sender.Send(new GetInventoryReportQuery())).LedgerMatches);
        var moves = await _h.Sender.Send(new GetStockMovesQuery(_h.Clock.Today, _h.Clock.Today));
        Assert.Contains(moves, m => m.Reason == StockMoveReason.Return && m.Note == refunds[0].Number);
    }

    [Fact]
    public async Task Numbers_AreSequential_AndTheSameReturnIdRecordsOnce()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 5_000, GstSlab.Zero);
        var bill = await SellAsync(TenderType.Cash, null, Discount.None, (rice, 4_000));
        var id = Guid.NewGuid();
        var cmd = new RecordReturnCommand(id, bill.Id, [new ReturnRequestLine(bill.Lines[0].Id, new Qty(1_000))], RefundMethod.Cash);
        var first = await _h.Sender.Send(cmd);
        var again = await _h.Sender.Send(cmd);
        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Code : "");
        Assert.Equal("R-000001", first.Value.Number);
        Assert.Equal(first.Value, again.Value);
        Assert.Equal(10_000 - 4_000 + 1_000, await _h.StockAsync(rice.Id));
        Assert.Single((await _h.Sender.Send(new GetBillReturnsQuery(bill.Id)))!.Returns);

        var second = await ReturnAsync(bill, RefundMethod.Cash, (0, 1_000));
        Assert.Equal("R-000002", second.Value.Number);

        // The same id for another bill is refused, not silently mapped to the first return.
        var other = await SellAsync(TenderType.Cash, null, Discount.None, (rice, 1_000));
        var reused = await _h.Sender.Send(new RecordReturnCommand(id, other.Id, [new ReturnRequestLine(other.Lines[0].Id, new Qty(1_000))], RefundMethod.Cash));
        Assert.Equal(ErrorCodes.ValidationFailed, reused.Error.Code);
    }

    [Fact]
    public async Task QuantityLimits_AreEnforced_Cumulatively()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 5_000, GstSlab.Zero);
        var bill = await SellAsync(TenderType.Cash, null, Discount.None, (rice, 2_000));

        Assert.Equal(ErrorCodes.ReturnQtyTooLarge, (await ReturnAsync(bill, RefundMethod.Cash, (0, 2_001))).Error.Code);
        Assert.True((await ReturnAsync(bill, RefundMethod.Cash, (0, 1_500))).IsSuccess);
        Assert.Equal(ErrorCodes.ReturnQtyTooLarge, (await ReturnAsync(bill, RefundMethod.Cash, (0, 501))).Error.Code);
        // Two requests for one line in one return count together.
        var twice = await _h.Sender.Send(new RecordReturnCommand(Guid.NewGuid(), bill.Id,
            [new ReturnRequestLine(bill.Lines[0].Id, new Qty(300)), new ReturnRequestLine(bill.Lines[0].Id, new Qty(300))], RefundMethod.Cash));
        Assert.Equal(ErrorCodes.ReturnQtyTooLarge, twice.Error.Code);
        Assert.True((await ReturnAsync(bill, RefundMethod.Cash, (0, 500))).IsSuccess);
        Assert.Equal(ErrorCodes.ReturnQtyTooLarge, (await ReturnAsync(bill, RefundMethod.Cash, (0, 1))).Error.Code);

        Assert.Equal(ErrorCodes.ReturnEmpty, (await _h.Sender.Send(new RecordReturnCommand(Guid.NewGuid(), bill.Id, [], RefundMethod.Cash))).Error.Code);
        Assert.Equal(ErrorCodes.ReturnEmpty, (await ReturnAsync(bill, RefundMethod.Cash, (0, 0))).Error.Code);
        Assert.Equal(ErrorCodes.BillQtyInvalid, (await ReturnAsync(bill, RefundMethod.Cash, (0, -1))).Error.Code);
        var foreign = await _h.Sender.Send(new RecordReturnCommand(Guid.NewGuid(), bill.Id, [new ReturnRequestLine(Guid.NewGuid(), new Qty(1))], RefundMethod.Cash));
        Assert.Equal(ErrorCodes.ValidationFailed, foreign.Error.Code);
        Assert.Equal(ErrorCodes.BillNotFound, (await _h.Sender.Send(new RecordReturnCommand(Guid.NewGuid(), Guid.NewGuid(), [new ReturnRequestLine(Guid.NewGuid(), new Qty(1))], RefundMethod.Cash))).Error.Code);
        Assert.Equal(10_000, await _h.StockAsync(rice.Id));
    }

    [Fact]
    public async Task DuesRefund_NeverBelowZero_ExcessGoesBackAsCash()
    {
        await _h.SeedShopAsync();
        var ravi = await CustomerAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 5_000, GstSlab.Zero);
        var bill = await SellAsync(TenderType.Udhaar, ravi, Discount.None, (rice, 2_000));   // ₹100 on udhaar
        Assert.True((await _h.Sender.Send(new RecordDuesPaymentCommand(ravi, new Paise(7_000), null))).IsSuccess);
        Assert.Equal(3_000, await DuesAsync(ravi));

        var r = await ReturnAsync(bill, RefundMethod.Dues, (0, 2_000));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code : "");
        Assert.Equal(10_000, r.Value.RefundTotal.Value);
        Assert.Equal(3_000, r.Value.DuesCredit.Value);
        Assert.Equal(7_000, r.Value.CashRefund.Value);
        Assert.Equal(0, await DuesAsync(ravi));
        Assert.Equal(await DuesAsync(ravi), await LedgerDuesAsync(ravi));

        var day = await _h.Sender.Send(new GetDayEndReportQuery(_h.Clock.Today));
        Assert.Equal(3_000, day.DuesCredited.Value);
        Assert.Equal(7_000, day.CashRefunds.Value);
        Assert.Equal(7_000, day.DuesCollected.Value);
    }

    [Fact]
    public async Task DuesRefund_NeedsTheBillsCustomer()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 5_000, GstSlab.Zero);
        var bill = await SellAsync(TenderType.Cash, null, Discount.None, (rice, 1_000));
        Assert.Equal(ErrorCodes.ReturnNeedsCustomer, (await ReturnAsync(bill, RefundMethod.Dues, (0, 1_000))).Error.Code);
        Assert.Equal(9_000, await _h.StockAsync(rice.Id));
    }

    [Fact]
    public async Task VoidIsBlockedAfterAReturn_AndReturnsNeedAFinalizedBill()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 5_000, GstSlab.Zero);
        var returned = await SellAsync(TenderType.Cash, null, Discount.None, (rice, 2_000));
        Assert.True((await ReturnAsync(returned, RefundMethod.Cash, (0, 1_000))).IsSuccess);
        Assert.Equal(ErrorCodes.BillHasReturns, (await _h.Sender.Send(new VoidBillCommand(returned.Id, "mistake"))).Error.Code);
        Assert.Equal(BillStatus.Finalized, (await _h.Sender.Send(new GetBillQuery(returned.Id)))!.Status);

        var voided = await SellAsync(TenderType.Cash, null, Discount.None, (rice, 1_000));
        Assert.True((await _h.Sender.Send(new VoidBillCommand(voided.Id, "mistake"))).IsSuccess);
        Assert.Equal(ErrorCodes.ReturnBillNotFinalized, (await ReturnAsync(voided, RefundMethod.Cash, (0, 1_000))).Error.Code);
        Assert.Null(await _h.Sender.Send(new GetBillReturnsQuery(Guid.NewGuid())));
        Assert.True((await _h.Sender.Send(new GetInventoryReportQuery())).LedgerMatches);
    }

    [Fact]
    public async Task GstSummary_NetsTheReturnInTheReturnsMonth_NotTheBills()
    {
        await _h.SeedShopAsync(Gstin);
        var oil = await _h.AddItemAsync("నూనె", 11_800, GstSlab.Eighteen, 100_000, Unit.Litre);
        var bill = await SellAsync(TenderType.Cash, null, Discount.None, (oil, 1_000));   // 19 Oct
        var october = await _h.Sender.Send(new GetGstSummaryQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        _h.Clock.Now = new DateTimeOffset(2026, 11, 3, 11, 0, 0, TimeSpan.FromHours(5.5));
        var r = await ReturnAsync(bill, RefundMethod.Cash, (0, 1_000));
        Assert.True(r.IsSuccess);

        var octoberAfter = await _h.Sender.Send(new GetGstSummaryQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.Equal(october.Sum(s => s.Tax.Value), octoberAfter.Sum(s => s.Tax.Value));   // a filed month never moves
        var november = Assert.Single(await _h.Sender.Send(new GetGstSummaryQuery(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))));
        Assert.Equal(-1_800, november.Tax.Value);
        Assert.Equal(-10_000, november.Taxable.Value);

        var nov3 = await _h.Sender.Send(new GetDayEndReportQuery(new DateOnly(2026, 11, 3)));
        Assert.Equal(0, nov3.SalesTotal.Value);
        Assert.Equal(11_800, nov3.Returns.Value);
        Assert.Equal(-11_800, nov3.NetSales.Value);
    }

    [Fact]
    public async Task CustomerAccount_ListsReturns_AndTheReturnNoteShares()
    {
        await _h.SeedShopAsync(Gstin);
        var ravi = await CustomerAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 5_000, GstSlab.Five);
        var bill = await SellAsync(TenderType.Cash, ravi, Discount.None, (rice, 2_000));
        var r = (await ReturnAsync(bill, RefundMethod.Upi, (0, 500))).Value;

        var listed = Assert.Single(await _h.Sender.Send(new GetCustomerReturnsQuery(ravi)));
        Assert.Equal(r.Number, listed.Number);
        Assert.Equal(bill.Number, listed.BillNumber);
        Assert.Equal(RefundMethod.Upi, listed.Method);

        Assert.True((await _h.Sender.Send(new ShareReturnNoteCommand(r.ReturnId))).IsSuccess);
        Assert.EndsWith(r.Number + ".pdf", _h.Share.SharedFiles.Last(), StringComparison.Ordinal);
        Assert.True((await _h.Sender.Send(new ShareReturnNoteCommand(Guid.NewGuid()))).IsFailure);
    }

    [Fact]
    public async Task ReturnNoteLayout_ShowsNumbers_Lines_TaxReversed_AndRefund()
    {
        await _h.SeedShopAsync(Gstin);
        var rice = await _h.AddItemAsync("బియ్యం", 5_250, GstSlab.Five);
        var bill = await SellAsync(TenderType.Cash, null, Discount.None, (rice, 2_000));
        var r = (await ReturnAsync(bill, RefundMethod.Cash, (0, 1_000))).Value;
        var ret = (await _h.Sender.Send(new GetBillReturnsQuery(bill.Id)))!.Returns.Single();
        var shop = (await _h.Sender.Send(new GetShopProfileQuery()))!;
        var text = (Angadi.Application.Platform.ILocalizer)_h.Services.GetService(typeof(Angadi.Application.Platform.ILocalizer))!;

        var rows = ReceiptLayout.BuildReturn(ret, bill, shop, text);
        Assert.Contains(rows, x => x.Kind == ReceiptRowKind.Field && x.Value == r.Number);
        Assert.Contains(rows, x => x.Kind == ReceiptRowKind.Field && x.Value == bill.Number);
        Assert.Contains(rows, x => x.Kind == ReceiptRowKind.Item);
        Assert.Contains(rows, x => x.Kind == ReceiptRowKind.Tax);
        var total = Assert.Single(rows, x => x.Kind == ReceiptRowKind.GrandTotal);
        Assert.Equal(IndianFormat.Rupees(r.RefundTotal), total.Value);
        Assert.DoesNotContain(rows, x => x.Kind == ReceiptRowKind.Qr);
    }

    [Fact]
    public async Task SplitReceipt_ShowsEveryPart()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6_250, GstSlab.Zero);
        var draft = AppHost.Draft(TenderType.Split, (rice, 2_000));
        draft.Payments =
        [
            new BillPayment { Tender = TenderType.Cash, Amount = new Paise(5_000), CashReceived = new Paise(10_000) },
            new BillPayment { Tender = TenderType.Upi, Amount = new Paise(7_500), UpiRef = "4242" },
        ];
        var done = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(done.IsSuccess);
        var bill = (await _h.Sender.Send(new GetBillQuery(done.Value.BillId)))!;
        var shop = (await _h.Sender.Send(new GetShopProfileQuery()))!;
        var text = (Angadi.Application.Platform.ILocalizer)_h.Services.GetService(typeof(Angadi.Application.Platform.ILocalizer))!;
        var rows = ReceiptLayout.Build(bill, shop, text);
        Assert.Contains(rows, x => x.Value == IndianFormat.Rupees(new Paise(5_000)));
        Assert.Contains(rows, x => x.Value == IndianFormat.Rupees(new Paise(7_500)));
        Assert.Contains(rows, x => x.Value == "4242");
        Assert.Contains(rows, x => x.Value == IndianFormat.Rupees(new Paise(10_000)));   // received on the cash part
        Assert.DoesNotContain(rows, x => x.Kind == ReceiptRowKind.Qr);
    }
}
