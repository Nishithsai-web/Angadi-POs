using System.Diagnostics;
using Angadi.Application.Persistence;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Persistence;
using Angadi.Tests.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Infrastructure;

/// <summary>
/// T2: a busy shop's month (~3,000 bills, mixed tenders and slabs, some voided, bills right at IST midnight):
/// every day-end, the month's GST summary and top items complete and add up to the bills that were written.
/// </summary>
public sealed class ReportScaleTests : IDisposable
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private sealed record Product(Guid Id, string Name, long Price, GstSlab Slab, Unit Unit);

    [Fact]
    public async Task AMonthOf3000Bills_DayEndGstSummaryAndTopItems_AreCompleteAndExact()
    {
        Product[] products =
        [
            new(Guid.NewGuid(), "బియ్యం", 6_250, GstSlab.Zero, Unit.Kilogram),
            new(Guid.NewGuid(), "నూనె", 19_900, GstSlab.Eighteen, Unit.Litre),
            new(Guid.NewGuid(), "పప్పు", 15_975, GstSlab.Five, Unit.Kilogram),
            new(Guid.NewGuid(), "పాన్ మసాలా", 1_099, GstSlab.Forty, Unit.Pack),
            new(Guid.NewGuid(), "సబ్బు", 4_500, GstSlab.Eighteen, Unit.Piece),
        ];
        var rng = new Random(3000);
        var written = new List<Bill>();
        for (var i = 0; i < 3_000; i++)
        {
            var day = 1 + i % 31;
            // The first and last bill of some days sit a second either side of IST midnight.
            var at = (i % 97) switch
            {
                0 => new DateTimeOffset(2026, 10, day, 0, 0, 1, Ist),
                1 => new DateTimeOffset(2026, 10, day, 23, 59, 59, Ist),
                _ => new DateTimeOffset(2026, 10, day, 0, 0, 0, Ist).AddSeconds(rng.Next(0, 86_400)),
            };
            var bill = new Bill
            {
                Number = $"A-{i + 1:D6}", Tender = (TenderType)rng.Next(0, 3), Status = BillStatus.Finalized, FinalizedAt = at,
                Lines = Enumerable.Range(0, rng.Next(1, 5)).Select(_ =>
                {
                    var p = products[rng.Next(products.Length)];
                    return new BillLine
                    {
                        ItemId = p.Id, TeluguName = p.Name, EnglishName = p.Name, Unit = p.Unit,
                        UnitPrice = new Paise(p.Price), GstSlab = p.Slab, Qty = new Qty(rng.Next(1, 5_000)),
                    };
                }).ToList(),
                BillDiscount = rng.Next(0, 5) == 0 ? Discount.Percent(rng.Next(1, 1_500)) : Discount.None,
            };
            BillCalculator.Compute(bill, shopHasGstin: true);
            if (i % 50 == 7)
            {
                bill.Status = BillStatus.Void;
                bill.VoidedAt = at.AddMinutes(5);
                bill.VoidReason = "wrong";
            }
            written.Add(bill);
        }

        var bills = _h.Services.GetRequiredService<IBillRepository>();
        await _h.Services.GetRequiredService<AngadiDb>().ExecuteAsync(async () =>
        {
            foreach (var b in written) await bills.SaveAsync(b);
            return true;
        }, ok => ok);

        var sold = written.Where(b => b.Status == BillStatus.Finalized).ToList();
        DateOnly DayOf(Bill b) => DateOnly.FromDateTime(b.FinalizedAt!.Value.DateTime);

        // Every day-end of the month.
        var watch = Stopwatch.StartNew();
        for (var d = new DateOnly(2026, 10, 1); d <= new DateOnly(2026, 10, 31); d = d.AddDays(1))
        {
            var day = d;
            var r = await _h.Sender.Send(new GetDayEndReportQuery(day));
            var mine = sold.Where(b => DayOf(b) == day).ToList();
            long TenderSum(TenderType t) => mine.Where(b => b.Tender == t).Sum(b => b.GrandTotal.Value);
            Assert.Equal(new DayEndReport(day, mine.Count, written.Count(b => b.Status == BillStatus.Void && DayOf(b) == day),
                new Paise(mine.Sum(b => b.GrandTotal.Value)), new Paise(TenderSum(TenderType.Cash)), new Paise(TenderSum(TenderType.Upi)),
                new Paise(TenderSum(TenderType.Udhaar)), new Paise(mine.Sum(b => BillCalculator.TotalDiscount(b).Value)),
                new Paise(mine.Sum(b => b.TaxTotal.Value))), r);
        }
        var dayEnds = watch.Elapsed;

        // The month's GST summary.
        watch.Restart();
        var gst = await _h.Sender.Send(new GetGstSummaryQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        var gstTime = watch.Elapsed;
        var expectedGst = sold.SelectMany(b => b.TaxSplits).GroupBy(s => s.Slab).OrderBy(g => (int)g.Key)
            .Select(g => new TaxSplit(g.Key, new Paise(g.Sum(s => s.Taxable.Value)), new Paise(g.Sum(s => s.Tax.Value))))
            .ToList();
        Assert.Equal(expectedGst, gst);
        Assert.Equal(sold.Sum(b => b.GrandTotal.Value - b.RoundingAdjustment.Value), gst.Sum(s => s.Taxable.Value + s.Tax.Value));

        // Top items over the month.
        var top = await _h.Sender.Send(new GetTopItemsQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Limit: 10));
        Assert.Equal(products.Length, top.Count);
        foreach (var t in top)
        {
            var lines = sold.SelectMany(b => b.Lines.Select(l => (b.Id, l))).Where(x => x.l.ItemId == t.ItemId).ToList();
            Assert.Equal(lines.Sum(x => x.l.Net.Value), t.Value.Value);
            Assert.Equal(lines.Sum(x => x.l.Qty.Milli), t.QtySold.Milli);
            Assert.Equal(lines.Select(x => x.Id).Distinct().Count(), t.BillCount);
        }
        Assert.Equal(top.Select(t => t.Value.Value).OrderDescending(), top.Select(t => t.Value.Value));

        // Soft guards against pathological slowness only (generous for a slow CI runner).
        Assert.True(gstTime < TimeSpan.FromSeconds(5), $"GST summary for 3,000 bills took {gstTime}");
        Assert.True(dayEnds < TimeSpan.FromSeconds(15), $"31 day-end reports took {dayEnds}");
    }
}
