using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Events;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Customers;

internal sealed class CustomerAccountHandlers(
    ICustomerRepository customers, IBillRepository bills, IOutbox outbox, IClock clock, ILocalizer text, IOptions<DuesOptions> options) :
    IRequestHandler<GetCustomerBillsQuery, IReadOnlyList<BillSummary>>,
    IRequestHandler<GetCustomerReportQuery, CustomerReport?>,
    IRequestHandler<SetPromisedPayDateCommand, Result>,
    IRequestHandler<GetDueRemindersQuery, IReadOnlyList<DueReminder>>
{
    private const int ReportBillLimit = 1000;   // a regular's whole history; summary math stays cheap

    public async Task<IReadOnlyList<BillSummary>> Handle(GetCustomerBillsQuery q, CancellationToken ct) =>
        (await bills.ListByCustomerAsync(q.CustomerId, Math.Clamp(q.Limit, 1, ReportBillLimit), ct))
            .Where(b => b.FinalizedAt is not null && b.Tender is not null)
            .Select(b => new BillSummary(b.Id, b.Number, b.FinalizedAt!.Value, b.GrandTotal, b.Tender!.Value, b.Status))
            .ToList();

    public async Task<CustomerReport?> Handle(GetCustomerReportQuery q, CancellationToken ct)
    {
        if (await customers.GetAsync(q.CustomerId, ct) is not { } customer) return null;
        var sold = (await bills.ListByCustomerAsync(q.CustomerId, ReportBillLimit, ct))
            .Where(b => b.Status == BillStatus.Finalized && b.FinalizedAt is not null).ToList();
        var ledger = await customers.GetLedgerAsync(q.CustomerId, ct);

        long total = sold.Sum(b => b.GrandTotal.Value);
        long By(TenderType t) => sold.Where(b => b.Tender == t).Sum(b => b.GrandTotal.Value);

        var top = sold.SelectMany(b => b.Lines.Select(l => (bill: b.Id, line: l)))
            .GroupBy(x => x.line.ItemId)
            .Select(g => new CustomerTopItem(g.Key, text.Name(g.First().line), g.First().line.Unit,
                new Qty(g.Sum(x => x.line.Qty.Milli)), new Paise(g.Sum(x => x.line.Net.Value)), g.Select(x => x.bill).Distinct().Count()))
            .OrderByDescending(t => t.Times).ThenByDescending(t => t.Value.Value).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(5).ToList();

        return new CustomerReport(
            customer.Id, sold.Count, new Paise(total), new Paise(sold.Count == 0 ? 0 : (total + sold.Count / 2) / sold.Count),
            sold.Count == 0 ? null : sold.Min(b => b.FinalizedAt), sold.Count == 0 ? null : sold.Max(b => b.FinalizedAt),
            new Paise(By(TenderType.Cash)), new Paise(By(TenderType.Upi)), new Paise(By(TenderType.Udhaar)),
            customer.DuesBalance,
            new Paise(ledger.Where(e => e.Kind == DuesEntryKind.Payment).Sum(e => e.Amount.Value)),
            DuesAging.OldestUnpaidDays(ledger, clock.Today),
            customer.PromisedPayDate,
            top);
    }

    public async Task<Result> Handle(SetPromisedPayDateCommand command, CancellationToken ct)
    {
        if (await customers.GetAsync(command.CustomerId, ct) is not { } customer) return Result.Failure(ErrorCodes.CustomerNotFound);
        customer.PromisedPayDate = command.Date;
        await customers.SaveAsync(customer, ct);
        await outbox.AppendAsync("Customer", new CustomerChanged(customer.Id, clock.Now), ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<DueReminder>> Handle(GetDueRemindersQuery q, CancellationToken ct)
    {
        var o = options.Value;
        var today = clock.Today;
        var due = new List<DueReminder>();
        foreach (var c in await customers.GetWithDuesAsync(ct))
        {
            if (c.DuesBalance.Value <= 0) continue;
            if (c.LastRemindedAt is { } last && (clock.Now - last).TotalDays < o.RemindAgainAfterDays) continue;

            var age = DuesAging.OldestUnpaidDays(await customers.GetLedgerAsync(c.Id, ct), today) ?? 0;
            DueReason? reason = c.PromisedPayDate is { } promised
                ? (promised <= today ? DueReason.PromiseDatePassed : null)     // a promise not yet due wins over age
                : (age >= o.RemindAfterDays ? DueReason.UnpaidTooLong : null);
            if (reason is { } r) due.Add(new DueReminder(c.Id, c.Name, c.Phone, c.DuesBalance, age, c.PromisedPayDate, c.LastRemindedAt, r));
        }
        return due.OrderByDescending(d => d.Reason == DueReason.PromiseDatePassed).ThenByDescending(d => d.Balance.Value).ToList();
    }
}

/// <summary>How long the oldest still-unpaid udhaar has been outstanding.</summary>
public static class DuesAging
{
    /// <summary>
    /// Reversals cancel their own bill's charge; payments then settle the oldest charges first (FIFO).
    /// Returns the age in days of the first charge not fully paid, or null when nothing is owed.
    /// </summary>
    public static int? OldestUnpaidDays(IEnumerable<DuesEntry> ledger, DateOnly today)
    {
        var entries = ledger.ToList();
        var reversed = entries.Where(e => e.Kind == DuesEntryKind.Reversal && e.BillId is not null).Select(e => e.BillId!.Value).ToHashSet();
        var charges = entries.Where(e => e.Kind == DuesEntryKind.Charge && (e.BillId is null || !reversed.Contains(e.BillId.Value)))
            .OrderBy(e => e.CreatedAt).ToList();
        var paid = entries.Where(e => e.Kind == DuesEntryKind.Payment).Sum(e => e.Amount.Value)
                 + entries.Where(e => e.Kind == DuesEntryKind.Reversal && e.BillId is null).Sum(e => e.Amount.Value);

        foreach (var charge in charges)
        {
            if (paid >= charge.Amount.Value) { paid -= charge.Amount.Value; continue; }
            return Math.Max(0, today.DayNumber - DateOnly.FromDateTime(charge.CreatedAt.ToLocalTime().DateTime).DayNumber);
        }
        return null;
    }
}
