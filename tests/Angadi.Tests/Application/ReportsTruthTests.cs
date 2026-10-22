using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;

namespace Angadi.Tests.Application;

/// <summary>
/// T2: the day-end report, the GST summary and top items must add up to exactly the bills behind them —
/// voids excluded, each bill on the right IST day, every slab's taxable + tax matching the bills.
/// </summary>
public sealed class ReportsTruthTests : IDisposable
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private const string Gstin = "36ABCDE1234F1Z5";
    private readonly AppHost _h = new();
    public ReportsTruthTests() => _h.Subscription.IsPro = true;
    public void Dispose() => _h.Dispose();

    private static DateTimeOffset At(int month, int day, int hour, int minute, int second = 0) =>
        new(2026, month, day, hour, minute, second, Ist);

    private async Task<Guid> CustomerAsync(string name = "రవి") =>
        (await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = name }))).Value;

    private async Task<Bill> SellAsync(TenderType tender, Guid? customer, params (Item item, long qtyMilli)[] lines) =>
        await SellAsync(tender, customer, Discount.None, lines);

    private async Task<Bill> SellAsync(TenderType tender, Guid? customer, Discount billDiscount, params (Item item, long qtyMilli)[] lines)
    {
        var draft = AppHost.Draft(tender, lines);
        draft.CustomerId = customer;
        draft.BillDiscount = billDiscount;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.Error.Code);
        var saved = await _h.Sender.Send(new GetBillQuery(r.Value.BillId));
        Assert.NotNull(saved);
        return saved;
    }

    private static long Total(IEnumerable<Bill> bills) => bills.Sum(b => b.GrandTotal.Value);

    // ---------- Day-end ----------

    [Fact]
    public async Task DayEnd_TotalsByTender_BillCount_Discounts_Tax_AreExactlyTheDaysBills_VoidsExcluded()
    {
        await _h.SeedShopAsync(Gstin);
        var ravi = await CustomerAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6_250, GstSlab.Five, 100_000);
        var oil = await _h.AddItemAsync("నూనె", 19_900, GstSlab.Eighteen, 100_000, Unit.Litre);
        var sugar = await _h.AddItemAsync("చక్కెర", 4_550, GstSlab.Zero, 100_000);

        var discounted = AppHost.Draft(TenderType.Cash, (rice, 1_500));
        discounted.Lines[0].LineDiscount = Discount.Percent(1000);
        var r1 = await _h.Sender.Send(new FinalizeBillCommand(discounted));
        Assert.True(r1.IsSuccess);
        var cash1 = (await _h.Sender.Send(new GetBillQuery(r1.Value.BillId)))!;
        var cash2 = await SellAsync(TenderType.Cash, null, (oil, 1_000), (sugar, 333));
        var upi1 = await SellAsync(TenderType.Upi, null, (sugar, 2_000), (rice, 250));
        var udhaar = await SellAsync(TenderType.Udhaar, ravi, Discount.Flat(new Paise(1_500)), (oil, 2_000), (rice, 1_000));
        var voided = await SellAsync(TenderType.Upi, null, (oil, 5_000));
        Assert.True((await _h.Sender.Send(new VoidBillCommand(voided.Id, "customer returned"))).IsSuccess);

        // A dues payment is money collected, not a sale: it must not inflate sales or the bill count.
        Assert.True((await _h.Sender.Send(new RecordDuesPaymentCommand(ravi, new Paise(5_000), null))).IsSuccess);

        var sold = new[] { cash1, cash2, upi1, udhaar };
        var r = await _h.Sender.Send(new GetDayEndReportQuery(_h.Clock.Today));
        Assert.Equal(_h.Clock.Today, r.Day);
        Assert.Equal(4, r.BillCount);
        Assert.Equal(1, r.VoidCount);
        Assert.Equal(Total(sold), r.SalesTotal.Value);
        Assert.Equal(cash1.GrandTotal.Value + cash2.GrandTotal.Value, r.Cash.Value);
        Assert.Equal(upi1.GrandTotal.Value, r.Upi.Value);
        Assert.Equal(udhaar.GrandTotal.Value, r.Udhaar.Value);
        Assert.Equal(r.SalesTotal.Value, r.Cash.Value + r.Upi.Value + r.Udhaar.Value);
        Assert.Equal(sold.Sum(b => b.TaxTotal.Value), r.Tax.Value);
        Assert.True(r.Tax.Value > 0);
        Assert.Equal(sold.Sum(b => BillCalculator.TotalDiscount(b).Value), r.Discounts.Value);
        Assert.Equal(1_500 + cash1.Lines[0].Gross.Value - cash1.Lines[0].LineTotal.Value, r.Discounts.Value);

        // The bill list agrees with the report.
        var list = await _h.Sender.Send(new ListBillsQuery(_h.Clock.Today, _h.Clock.Today));
        Assert.Equal(5, list.Count);
        Assert.Equal(r.SalesTotal.Value, list.Where(b => b.Status == BillStatus.Finalized).Sum(b => b.GrandTotal.Value));
    }

    [Fact]
    public async Task DayEnd_AcrossIstMidnight_EachBillLandsOnItsOwnDay()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6_000, stockMilli: 100_000);

        _h.Clock.Now = At(10, 19, 23, 59, 30);                    // 18:29 UTC, Oct 19
        var late = await SellAsync(TenderType.Cash, null, (rice, 1_000));
        _h.Clock.Now = At(10, 20, 0, 0, 30);                      // 18:30 UTC, still Oct 19 in UTC
        var early = await SellAsync(TenderType.Upi, null, (rice, 2_000));
        _h.Clock.Now = At(10, 20, 5, 15);                         // 23:45 UTC Oct 19
        var dawn = await SellAsync(TenderType.Cash, null, (rice, 3_000));

        var d19 = await _h.Sender.Send(new GetDayEndReportQuery(new DateOnly(2026, 10, 19)));
        var d20 = await _h.Sender.Send(new GetDayEndReportQuery(new DateOnly(2026, 10, 20)));
        Assert.Equal(1, d19.BillCount);
        Assert.Equal(late.GrandTotal, d19.SalesTotal);
        Assert.Equal(late.GrandTotal, d19.Cash);
        Assert.Equal(2, d20.BillCount);
        Assert.Equal(early.GrandTotal.Value + dawn.GrandTotal.Value, d20.SalesTotal.Value);
        Assert.Equal(early.GrandTotal, d20.Upi);
        Assert.Equal(dawn.GrandTotal, d20.Cash);

        Assert.Single(await _h.Sender.Send(new ListBillsQuery(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 19))));
        Assert.Equal(3, (await _h.Sender.Send(new ListBillsQuery(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 20)))).Count);
    }

    [Fact]
    public async Task DayEnd_ForADayWithNoBills_IsAllZero_AndOtherDaysAreUntouched()
    {
        await _h.SeedShopAsync(Gstin);
        var rice = await _h.AddItemAsync("బియ్యం", 6_000, stockMilli: 100_000);
        var sold = await SellAsync(TenderType.Cash, null, (rice, 1_000));

        foreach (var day in new[] { _h.Clock.Today.AddDays(-1), _h.Clock.Today.AddDays(1), new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31) })
        {
            var r = await _h.Sender.Send(new GetDayEndReportQuery(day));
            Assert.Equal(new DayEndReport(day, 0, 0, Paise.Zero, Paise.Zero, Paise.Zero, Paise.Zero, Paise.Zero, Paise.Zero), r);
        }
        Assert.Equal(sold.GrandTotal, (await _h.Sender.Send(new GetDayEndReportQuery(_h.Clock.Today))).SalesTotal);
    }

    [Fact]
    public async Task DayEnd_OnAnEmptyShop_DoesNotThrow()
    {
        var r = await _h.Sender.Send(new GetDayEndReportQuery(_h.Clock.Today));
        Assert.Equal(0, r.BillCount);
        Assert.Equal(Paise.Zero, r.SalesTotal);
    }

    [Fact]
    public async Task DayEnd_VoidOnALaterDay_DoesNotRewriteTheClosedDay_AndShowsOnTheVoidDay()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6_000, stockMilli: 100_000);
        _h.Clock.Now = At(10, 19, 18, 0);
        var monday = await SellAsync(TenderType.Cash, null, (rice, 2_000));
        var closedMonday = await _h.Sender.Send(new GetDayEndReportQuery(new DateOnly(2026, 10, 19)));

        _h.Clock.Now = At(10, 20, 10, 0);   // Tuesday: the customer brings it back; cash goes out of Tuesday's drawer
        Assert.True((await _h.Sender.Send(new VoidBillCommand(monday.Id, "returned"))).IsSuccess);

        Assert.Equal(closedMonday, await _h.Sender.Send(new GetDayEndReportQuery(new DateOnly(2026, 10, 19))));
        Assert.Equal(1, (await _h.Sender.Send(new GetDayEndReportQuery(new DateOnly(2026, 10, 20)))).VoidCount);
    }

    // ---------- GST summary ----------

    [Fact]
    public async Task GstSummary_MonthToDate_PerSlabEqualsTheMonthsFinalizedBills_VoidsAndOtherMonthsExcluded()
    {
        await _h.SeedShopAsync(Gstin);
        var ravi = await CustomerAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6_250, GstSlab.Five, 1_000_000);
        var oil = await _h.AddItemAsync("నూనె", 19_900, GstSlab.Eighteen, 1_000_000, Unit.Litre);
        var dal = await _h.AddItemAsync("పప్పు", 15_975, GstSlab.Zero, 1_000_000);
        var pan = await _h.AddItemAsync("పాన్ మసాలా", 1_099, GstSlab.Forty, 1_000_000, Unit.Pack);

        _h.Clock.Now = At(9, 30, 23, 59, 59);
        await SellAsync(TenderType.Cash, null, (rice, 5_000), (oil, 1_000));          // September: excluded

        var october = new List<Bill>();
        _h.Clock.Now = At(10, 1, 0, 0, 1);
        october.Add(await SellAsync(TenderType.Cash, null, (rice, 1_234), (oil, 1_000), (dal, 750), (pan, 3_000)));
        var rng = new Random(2026);
        for (var day = 2; day <= 31; day++)
        {
            _h.Clock.Now = At(10, day, 9 + rng.Next(0, 12), rng.Next(0, 60));
            var tender = (TenderType)rng.Next(0, 3);
            var discount = rng.Next(0, 3) switch { 0 => Discount.None, 1 => Discount.Percent(rng.Next(1, 2_000)), _ => Discount.Flat(new Paise(rng.Next(1, 3_000))) };
            october.Add(await SellAsync(tender, tender == TenderType.Udhaar ? ravi : null, discount,
                (rice, rng.Next(1, 5_000)), (oil, rng.Next(1, 3_000)), (dal, rng.Next(1, 2_000)), (pan, rng.Next(1_000, 5_000))));
        }
        _h.Clock.Now = At(10, 31, 23, 59, 59);
        october.Add(await SellAsync(TenderType.Upi, null, (pan, 7_000), (oil, 333)));
        // Voids are excluded, whichever day they were voided on.
        var voided = october.Single(b => b.FinalizedAt!.Value.Day == 10);
        Assert.True((await _h.Sender.Send(new VoidBillCommand(voided.Id, "wrong"))).IsSuccess);
        october.Remove(voided);

        _h.Clock.Now = At(11, 1, 0, 0, 1);
        await SellAsync(TenderType.Cash, null, (oil, 4_000));                           // November: excluded

        AssertSummaryMatches(october, await _h.Sender.Send(new GetGstSummaryQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))));

        // Month-to-date, as the Reports page asks for it (1st → picked day).
        var toThe15th = october.Where(b => b.FinalizedAt!.Value.Day <= 15).ToList();
        var mtd = await _h.Sender.Send(new GetGstSummaryQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15)));
        AssertSummaryMatches(toThe15th, mtd);

        // Per-slab taxable + tax is exactly the GST-inclusive net of that slab's lines, and the summary + rounding
        // is the month's sales (the day-end totals added up).
        foreach (var s in mtd)
            Assert.Equal(toThe15th.SelectMany(b => b.Lines).Where(l => l.GstSlab == s.Slab).Sum(l => l.Net.Value), s.Taxable.Value + s.Tax.Value);
        long dayEndSales = 0, dayEndTax = 0;
        for (var d = new DateOnly(2026, 10, 1); d <= new DateOnly(2026, 10, 15); d = d.AddDays(1))
        {
            var r = await _h.Sender.Send(new GetDayEndReportQuery(d));
            dayEndSales += r.SalesTotal.Value;
            dayEndTax += r.Tax.Value;
        }
        // T2-01: the bill sold on the 10th and voided on the 31st stays in the 10th's CLOSED day-end (that cash was in
        // that day's drawer) but is not a supply for GST, so the day-ends exceed the GST summary by exactly that bill.
        Assert.Equal(dayEndSales - voided.GrandTotal.Value, mtd.Sum(s => s.Taxable.Value + s.Tax.Value) + toThe15th.Sum(b => b.RoundingAdjustment.Value));
        Assert.Equal(dayEndTax - voided.TaxTotal.Value, mtd.Sum(s => s.Tax.Value));
    }

    private static void AssertSummaryMatches(IReadOnlyList<Bill> bills, IReadOnlyList<TaxSplit> summary)
    {
        var expected = bills.SelectMany(b => b.TaxSplits)
            .GroupBy(s => s.Slab)
            .OrderBy(g => (int)g.Key)
            .Select(g => new TaxSplit(g.Key, new Paise(g.Sum(s => s.Taxable.Value)), new Paise(g.Sum(s => s.Tax.Value))))
            .ToList();
        Assert.Equal(expected, summary);
        Assert.Equal(summary.Select(s => (int)s.Slab).Order(), summary.Select(s => (int)s.Slab));   // slabs ascending
        Assert.Equal(summary.Count, summary.Select(s => s.Slab).Distinct().Count());               // one row per slab
    }

    [Fact]
    public async Task GstSummary_BillOfSupplyShop_IsEmpty_DayEndTaxIsZero_ButSalesStillCount()
    {
        await _h.SeedShopAsync();   // no GSTIN → Bill of Supply
        var rice = await _h.AddItemAsync("బియ్యం", 6_250, GstSlab.Five, 100_000);
        var bill = await SellAsync(TenderType.Cash, null, (rice, 2_000));
        Assert.False(bill.IsTaxInvoice);

        Assert.Empty(await _h.Sender.Send(new GetGstSummaryQuery(_h.Clock.Today, _h.Clock.Today)));
        var r = await _h.Sender.Send(new GetDayEndReportQuery(_h.Clock.Today));
        Assert.Equal(Paise.Zero, r.Tax);
        Assert.Equal(bill.GrandTotal, r.SalesTotal);
    }

    [Fact]
    public async Task GstSummary_ShopRegistersMidMonth_OnlyTaxInvoicesCarryTax()
    {
        await _h.SeedShopAsync();
        var oil = await _h.AddItemAsync("నూనె", 19_900, GstSlab.Eighteen, 100_000, Unit.Litre);
        _h.Clock.Now = At(10, 5, 11, 0);
        var before = await SellAsync(TenderType.Cash, null, (oil, 1_000));
        await _h.SeedShopAsync(Gstin);
        _h.Clock.Now = At(10, 12, 11, 0);
        var after = await SellAsync(TenderType.Cash, null, (oil, 1_000));
        Assert.False(before.IsTaxInvoice);
        Assert.True(after.IsTaxInvoice);

        var summary = await _h.Sender.Send(new GetGstSummaryQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.Equal(after.TaxSplits, summary);
    }

    [Fact]
    public async Task GstSummary_EmptyRange_AndReversedRange_AreEmpty()
    {
        await _h.SeedShopAsync(Gstin);
        var oil = await _h.AddItemAsync("నూనె", 19_900, GstSlab.Eighteen, 100_000, Unit.Litre);
        await SellAsync(TenderType.Cash, null, (oil, 1_000));
        Assert.Empty(await _h.Sender.Send(new GetGstSummaryQuery(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30))));
        Assert.Empty(await _h.Sender.Send(new GetGstSummaryQuery(_h.Clock.Today, _h.Clock.Today.AddDays(-1))));
    }

    // ---------- Top items ----------

    [Fact]
    public async Task TopItems_ByValueAndByBillCount_TiesByName_VoidsAndOutOfRangeExcluded()
    {
        await _h.SeedShopAsync();
        var a = await _h.AddItemAsync("A-atta", 10_000, GstSlab.Zero, 1_000_000, Unit.Piece);
        var b = await _h.AddItemAsync("B-bread", 5_000, GstSlab.Zero, 1_000_000, Unit.Piece);
        var c = await _h.AddItemAsync("C-curd", 5_000, GstSlab.Zero, 1_000_000, Unit.Piece);
        var d = await _h.AddItemAsync("D-dates", 1_000, GstSlab.Zero, 1_000_000, Unit.Piece);

        _h.Clock.Now = At(10, 9, 23, 59, 59);
        await SellAsync(TenderType.Cash, null, (d, 100_000));                       // the day before the range
        _h.Clock.Now = At(10, 10, 0, 0, 1);
        await SellAsync(TenderType.Cash, null, (c, 2_000), (b, 2_000));             // first second of the range
        _h.Clock.Now = At(10, 11, 12, 0);
        await SellAsync(TenderType.Upi, null, (d, 1_000), (a, 1_000), (d, 1_000));  // D twice on one bill = one bill
        var voided = await SellAsync(TenderType.Cash, null, (b, 50_000));
        Assert.True((await _h.Sender.Send(new VoidBillCommand(voided.Id, "wrong"))).IsSuccess);
        _h.Clock.Now = At(10, 12, 23, 59, 59);
        await SellAsync(TenderType.Cash, null, (d, 1_000));                         // last second of the range
        _h.Clock.Now = At(10, 13, 0, 0, 1);
        await SellAsync(TenderType.Cash, null, (c, 100_000));                       // the day after the range

        var from = new DateOnly(2026, 10, 10);
        var to = new DateOnly(2026, 10, 12);
        var byValue = await _h.Sender.Send(new GetTopItemsQuery(from, to));
        var byBills = await _h.Sender.Send(new GetTopItemsQuery(from, to, Sort: TopItemsSort.BillCount));

        // A, B and C tie on ₹100 and one bill each → by name; D (₹30) is last.
        Assert.Equal([a.Id, b.Id, c.Id, d.Id], byValue.Select(t => t.ItemId));
        Assert.Equal([10_000L, 10_000L, 10_000L, 3_000L], byValue.Select(t => t.Value.Value));
        var dates = byValue.Single(t => t.ItemId == d.Id);
        Assert.Equal(2, dates.BillCount);
        Assert.Equal(3_000, dates.QtySold.Milli);
        Assert.Equal(1, byValue.Single(t => t.ItemId == b.Id).BillCount);   // the voided 50-piece bill is not counted

        // D moved on the most bills; the rest tie on bills and value → by name.
        Assert.Equal([d.Id, a.Id, b.Id, c.Id], byBills.Select(t => t.ItemId));

        Assert.Equal([a.Id, b.Id], (await _h.Sender.Send(new GetTopItemsQuery(from, to, Limit: 2))).Select(t => t.ItemId));
        Assert.Empty(await _h.Sender.Send(new GetTopItemsQuery(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))));
        // Single-day ranges at each edge.
        Assert.Equal([b.Id, c.Id], (await _h.Sender.Send(new GetTopItemsQuery(from, from))).Select(t => t.ItemId));
        Assert.Equal(d.Id, Assert.Single(await _h.Sender.Send(new GetTopItemsQuery(to, to))).ItemId);
    }

    [Fact]
    public async Task TopItems_ValueIsNetOfLineAndBillDiscounts_AndSumsToTheLinesNet()
    {
        await _h.SeedShopAsync(Gstin);
        var rice = await _h.AddItemAsync("బియ్యం", 6_250, GstSlab.Five, 1_000_000);
        var oil = await _h.AddItemAsync("నూనె", 19_900, GstSlab.Eighteen, 1_000_000, Unit.Litre);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 3_000), (oil, 2_000));
        draft.Lines[0].LineDiscount = Discount.Percent(500);
        draft.BillDiscount = Discount.Flat(new Paise(2_345));
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess);
        var bill = (await _h.Sender.Send(new GetBillQuery(r.Value.BillId)))!;

        var top = await _h.Sender.Send(new GetTopItemsQuery(_h.Clock.Today, _h.Clock.Today));
        Assert.Equal(bill.Lines.Single(l => l.ItemId == rice.Id).Net, top.Single(t => t.ItemId == rice.Id).Value);
        Assert.Equal(bill.Lines.Single(l => l.ItemId == oil.Id).Net, top.Single(t => t.ItemId == oil.Id).Value);
        Assert.Equal(bill.SubTotal.Value - bill.BillDiscountAmount.Value, top.Sum(t => t.Value.Value));
    }
}
