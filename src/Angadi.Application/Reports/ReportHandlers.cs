using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;

namespace Angadi.Application.Reports;

/// <summary>FR-27–30 and the bill list (FR-20). Read-only; voided bills are excluded from money totals.</summary>
internal sealed class ReportHandlers(IBillRepository bills, ICustomerRepository customers, ILocalizer text, IClock clock, IReturnRepository returns) :
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
        // FIX(S1-F1, 2026-10-22, S1): Flaw: since T2-01 a bill voided on a later day stays in its closed day, but the
        // void day showed only a count — Tuesday's report claimed the cash handed back for Monday's bill was still in
        // the drawer. Fix: the day-end carries Refunds = totals of cash/UPI bills sold earlier and voided this day,
        // shown on Reports. Why: the drawer count must match the report. Logged in docs/AGENT_FIX_LOG.md.
        var refunds = await bills.SumRefundsAsync(dayStart, dayStart.AddDays(1), q.Day, ct);
        Paise Sum(IEnumerable<Bill> bs) => new(bs.Sum(b => b.GrandTotal.Value));
        // v2 split tender: tender totals sum payment parts (an old single-tender bill reads as one part).
        Paise By(TenderType t) => new(sold.Sum(b => BillPayments.AmountBy(b, t).Value));
        // v2 returns: on the day they happen, whatever day the bill was.
        var dayReturns = await returns.ListByDayAsync(q.Day, q.Day, ct);
        var collected = await customers.SumEntriesAsync(DuesEntryKind.Payment, dayStart, dayStart.AddDays(1), ct);

        return new DayEndReport(
            q.Day,
            BillCount: sold.Count,
            VoidCount: voidsToday,
            SalesTotal: Sum(sold),
            Cash: By(TenderType.Cash),
            Upi: By(TenderType.Upi),
            Udhaar: By(TenderType.Udhaar),
            Discounts: new Paise(sold.Sum(b => BillCalculator.TotalDiscount(b).Value)),
            Tax: new Paise(sold.Sum(b => b.TaxTotal.Value)),
            Refunds: new Paise(refunds),
            ReturnCount: dayReturns.Count,
            Returns: new Paise(dayReturns.Sum(r => r.RefundTotal.Value)),
            CashRefunds: new Paise(dayReturns.Sum(r => r.CashRefund.Value)),
            UpiRefunds: new Paise(dayReturns.Sum(r => r.UpiRefund.Value)),
            DuesCredited: new Paise(dayReturns.Sum(r => r.DuesCredit.Value)),
            DuesCollected: collected);
    }

    public async Task<IReadOnlyList<TaxSplit>> Handle(GetGstSummaryQuery q, CancellationToken ct)
    {
        var sold = (await bills.ListByDayAsync(q.From, q.To, ct)).Where(b => b.Status == BillStatus.Finalized);
        // v2 returns are credit notes: their GST comes off in the period the return happened, whatever the bill's month.
        var credited = (await returns.ListByDayAsync(q.From, q.To, ct))
            .SelectMany(r => r.TaxSplits).Select(s => new TaxSplit(s.Slab, -s.Taxable, -s.Tax));
        return sold.SelectMany(b => b.TaxSplits).Concat(credited)
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
