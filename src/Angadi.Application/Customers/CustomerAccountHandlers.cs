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
            top,
            // SEC-5: write-offs are reported on their own line — DuesPaidSoFar above counts payments only.
            new Paise(ledger.Where(e => e.Kind == DuesEntryKind.WriteOff).Sum(e => e.Amount.Value)));
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
    /// <summary>The udhaar still unpaid, oldest first: (when it was taken, the bill, what is left of it).</summary>
    public static IReadOnlyList<(DateTimeOffset At, Guid? BillId, Paise Remaining)> UnpaidCharges(IEnumerable<DuesEntry> ledger)
    {
        var entries = ledger.ToList();
        var reversed = entries.Where(e => e.Kind == DuesEntryKind.Reversal && e.BillId is not null).Select(e => e.BillId!.Value).ToHashSet();
        var charges = entries.Where(e => e.Kind == DuesEntryKind.Charge && (e.BillId is null || !reversed.Contains(e.BillId.Value)))
            .OrderBy(e => e.CreatedAt).ToList();
        // SEC-5: a write-off settles charges exactly like a payment for aging — the debt is gone, just not paid.
        var paid = entries.Where(e => e.Kind is DuesEntryKind.Payment or DuesEntryKind.WriteOff).Sum(e => e.Amount.Value)
                 + entries.Where(e => e.Kind == DuesEntryKind.Reversal && e.BillId is null).Sum(e => e.Amount.Value);
        var open = new List<(DateTimeOffset, Guid?, Paise)>();
        foreach (var charge in charges)
        {
            if (paid >= charge.Amount.Value) { paid -= charge.Amount.Value; continue; }
            open.Add((charge.CreatedAt, charge.BillId, new Paise(charge.Amount.Value - paid)));
            paid = 0;
        }
        return open;
    }

    public static int? OldestUnpaidDays(IEnumerable<DuesEntry> ledger, DateOnly today)
    {
        var entries = ledger.ToList();
        var reversed = entries.Where(e => e.Kind == DuesEntryKind.Reversal && e.BillId is not null).Select(e => e.BillId!.Value).ToHashSet();
        var charges = entries.Where(e => e.Kind == DuesEntryKind.Charge && (e.BillId is null || !reversed.Contains(e.BillId.Value)))
            .OrderBy(e => e.CreatedAt).ToList();
        // SEC-5: write-offs settle charges like payments (see UnpaidCharges).
        var paid = entries.Where(e => e.Kind is DuesEntryKind.Payment or DuesEntryKind.WriteOff).Sum(e => e.Amount.Value)
                 + entries.Where(e => e.Kind == DuesEntryKind.Reversal && e.BillId is null).Sum(e => e.Amount.Value);

        foreach (var charge in charges)
        {
            if (paid >= charge.Amount.Value) { paid -= charge.Amount.Value; continue; }
            return Math.Max(0, today.DayNumber - DateOnly.FromDateTime(charge.CreatedAt.ToLocalTime().DateTime).DayNumber);
        }
        return null;
    }
}

// ---------- Dues write-off (SEC-5) ----------

internal sealed class RecordDuesWriteOffValidator : IValidator<RecordDuesWriteOffCommand>
{
    public Error? Validate(RecordDuesWriteOffCommand c)
    {
        if (c.Amount.Value <= 0) return new Error(ErrorCodes.AmountInvalid);
        // The reason is the whole point of the kind: an unexplained forgiveness is indistinguishable
        // from the fake payment this command exists to replace.
        if (string.IsNullOrWhiteSpace(c.Reason)) return new Error(ErrorCodes.ValidationFailed, "write-off reason required");
        return null;
    }
}

/// <summary>SEC-5: bad debt is forgiven as a WriteOff entry — never as a fake payment.</summary>
internal sealed class RecordDuesWriteOffHandler(ICustomerRepository customers, IOutbox outbox, IClock clock)
    : IRequestHandler<RecordDuesWriteOffCommand, Result>
{
    public async Task<Result> Handle(RecordDuesWriteOffCommand command, CancellationToken ct)
    {
        var customer = await customers.GetAsync(command.CustomerId, ct);
        if (customer is null) return Result.Failure(ErrorCodes.CustomerNotFound);
        if (command.Amount > customer.DuesBalance)
            return Result.Failure(Error.With(ErrorCodes.DuesExceedsBalance,
                customer.DuesBalance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // FIX(SEC-5, 2026-10-20, FIX-4): Flaw: with no WriteOff kind, forgiving bad debt meant recording a
        // fake Payment — collections totals overstated money that actually arrived, and real payment fraud
        // could hide in the same bucket. Fix: a WriteOff ledger entry carrying the mandatory reason in its
        // Note; AddDuesEntryAsync already moves the balance down for every non-Charge kind, report totals
        // count only Kind == Payment as collected, and DuesAging settles charges with write-offs like
        // payments. Why: the ledger stays the single truth — forgiveness is visible as itself, forever.
        // Logged in docs/AGENT_FIX_LOG.md.
        await customers.AddDuesEntryAsync(new DuesEntry
        {
            CustomerId = customer.Id, Kind = DuesEntryKind.WriteOff, Amount = command.Amount, Note = command.Reason.Trim(),
        }, ct);
        await outbox.AppendAsync("Customer", new DuesWrittenOff(customer.Id, clock.Now, command.Amount.Value), ct);
        return Result.Success();
    }
}
