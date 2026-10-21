using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Application.Reports;

/// <summary>FR-27–30 and the bill list (FR-20). Read-only; voided bills are excluded from money totals.</summary>
internal sealed class ReportHandlers(IBillRepository bills, ICustomerRepository customers, ILocalizer text, IClock clock) :
    IRequestHandler<GetDayEndReportQuery, DayEndReport>,
    IRequestHandler<GetGstSummaryQuery, IReadOnlyList<TaxSplit>>,
    IRequestHandler<GetTopItemsQuery, IReadOnlyList<TopItem>>,
    IRequestHandler<GetDuesOutstandingQuery, DuesOutstanding>,
    IRequestHandler<ListBillsQuery, IReadOnlyList<BillSummary>>
{
    public async Task<DayEndReport> Handle(GetDayEndReportQuery q, CancellationToken ct)
    {
        // T2-01: a closed day never changes. A bill voided on a LATER day stays in the day it was sold (that cash was
        // in that day's drawer); the void is reported on the day it happened. A same-day void is not a sale at all.
        var all = await bills.ListByDayAsync(q.Day, q.Day, ct);
        var sold = all.Where(b => b.Status == BillStatus.Finalized
            || (b.Status == BillStatus.Void && b.VoidedAt is { } voided && DateOnly.FromDateTime(voided.DateTime) > q.Day)).ToList();
        var offset = clock.Now.Offset;
        var dayStart = new DateTimeOffset(q.Day.ToDateTime(TimeOnly.MinValue), offset);
        var voidsToday = await bills.CountVoidedAsync(dayStart, dayStart.AddDays(1), ct);
        Paise Sum(IEnumerable<Bill> bs) => new(bs.Sum(b => b.GrandTotal.Value));

        return new DayEndReport(
            q.Day,
            BillCount: sold.Count,
            VoidCount: voidsToday,
            SalesTotal: Sum(sold),
            Cash: Sum(sold.Where(b => b.Tender == TenderType.Cash)),
            Upi: Sum(sold.Where(b => b.Tender == TenderType.Upi)),
            Udhaar: Sum(sold.Where(b => b.Tender == TenderType.Udhaar)),
            Discounts: new Paise(sold.Sum(b => BillCalculator.TotalDiscount(b).Value)),
            Tax: new Paise(sold.Sum(b => b.TaxTotal.Value)));
    }

    public async Task<IReadOnlyList<TaxSplit>> Handle(GetGstSummaryQuery q, CancellationToken ct)
    {
        var sold = (await bills.ListByDayAsync(q.From, q.To, ct)).Where(b => b.Status == BillStatus.Finalized);
        return sold.SelectMany(b => b.TaxSplits)
            .GroupBy(s => s.Slab)
            .OrderBy(g => (int)g.Key)
            .Select(g => new TaxSplit(g.Key, new Paise(g.Sum(s => s.Taxable.Value)), new Paise(g.Sum(s => s.Tax.Value))))
            .ToList();
    }

    public async Task<IReadOnlyList<TopItem>> Handle(GetTopItemsQuery q, CancellationToken ct)
    {
        var sold = (await bills.ListByDayAsync(q.From, q.To, ct)).Where(b => b.Status == BillStatus.Finalized);
        var items = sold.SelectMany(b => b.Lines.Select(l => (bill: b.Id, line: l)))
            .GroupBy(x => x.line.ItemId)
            .Select(g =>
            {
                var first = g.First().line;
                var name = text.Name(first);
                return new TopItem(g.Key, name, first.Unit, new Qty(g.Sum(x => x.line.Qty.Milli)),
                    new Paise(g.Sum(x => x.line.Net.Value)), g.Select(x => x.bill).Distinct().Count());
            });
        var ordered = q.Sort == TopItemsSort.BillCount
            ? items.OrderByDescending(t => t.BillCount).ThenByDescending(t => t.Value.Value)
            : items.OrderByDescending(t => t.Value.Value).ThenByDescending(t => t.BillCount);
        return ordered
            .ThenBy(t => t.DisplayName, StringComparer.Ordinal)
            .Take(Math.Max(1, q.Limit))
            .ToList();
    }

    public async Task<DuesOutstanding> Handle(GetDuesOutstandingQuery q, CancellationToken ct)
    {
        var owing = (await customers.GetWithDuesAsync(ct)).Where(c => c.DuesBalance.Value > 0).ToList();
        return new DuesOutstanding(
            new Paise(owing.Sum(c => c.DuesBalance.Value)),
            owing.Select(c => new CustomerDue(c.Id, c.Name, c.Phone, c.DuesBalance)).ToList());
    }

    public async Task<IReadOnlyList<BillSummary>> Handle(ListBillsQuery q, CancellationToken ct) =>
        (await bills.ListByDayAsync(q.From, q.To, ct))
            .Where(b => b.FinalizedAt is not null && b.Tender is not null)
            .Select(b => new BillSummary(b.Id, b.Number, b.FinalizedAt!.Value, b.GrandTotal, b.Tender!.Value, b.Status))
            .ToList();
}
