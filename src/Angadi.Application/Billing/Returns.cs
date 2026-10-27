using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Events;
using Angadi.Domain.Inventory;

namespace Angadi.Application.Billing;

// v2 returns / partial refunds (docs/BILLING_RULES.md §Returns). A finalized bill never changes: goods brought back
// become a SalesReturn document of their own (R-000001), linked to the bill. Owner-only: the UI puts it behind the
// owner PIN, like void.

/// <summary>One bill line and how much of it comes back.</summary>
public sealed record ReturnRequestLine(Guid BillLineId, Qty Qty);

/// <summary>
/// Records a return against a finalized bill. <paramref name="ReturnId"/> is minted by the screen once per return, so
/// a double tap or a retry after a slow save records it once. Refund: cash, UPI (optional reference), or by reducing
/// the bill customer's udhaar — never below zero; whatever is left over goes back as cash.
/// </summary>
public sealed record RecordReturnCommand(
    Guid ReturnId, Guid BillId, IReadOnlyList<ReturnRequestLine> Lines, RefundMethod Method, string? UpiRef = null, string? Reason = null)
    : ICommand<Result<RecordedReturn>>;

public sealed record RecordedReturn(Guid ReturnId, string Number, Paise RefundTotal, Paise CashRefund, Paise UpiRefund, Paise DuesCredit);

/// <summary>What can still come back on a bill, line by line, and the returns already made.</summary>
public sealed record GetBillReturnsQuery(Guid BillId) : IQuery<BillReturns?>;
public sealed record BillReturns(Bill Bill, IReadOnlyList<ReturnableLine> Lines, IReadOnlyList<SalesReturn> Returns)
{
    public bool CanReturn => Bill.Status == BillStatus.Finalized && Lines.Any(l => l.Remaining.Milli > 0);
}
public sealed record ReturnableLine(BillLine Line, Qty Returned, Qty Remaining);

internal sealed class RecordReturnValidator : IValidator<RecordReturnCommand>
{
    public Error? Validate(RecordReturnCommand c)
    {
        if (c.Lines is null || c.Lines.Count == 0 || c.Lines.All(l => l.Qty.Milli == 0)) return new Error(ErrorCodes.ReturnEmpty);
        if (c.Lines.Any(l => l.Qty.Milli < 0)) return new Error(ErrorCodes.BillQtyInvalid);
        if (!Enum.IsDefined(c.Method)) return new Error(ErrorCodes.ValidationFailed, "unknown refund method");
        if (c.Method != RefundMethod.Upi && !string.IsNullOrWhiteSpace(c.UpiRef)) return new Error(ErrorCodes.ValidationFailed, "UPI reference on a non-UPI refund");
        return null;
    }
}

internal sealed class RecordReturnHandler(
    IBillRepository bills, IReturnRepository returns, ICustomerRepository customers, IStockLedger ledger, IOutbox outbox, IClock clock)
    : IRequestHandler<RecordReturnCommand, Result<RecordedReturn>>
{
    public async Task<Result<RecordedReturn>> Handle(RecordReturnCommand command, CancellationToken ct)
    {
        // Idempotent: the same return id twice (double tap, watchdog retry) is the return already recorded.
        if (await returns.GetAsync(command.ReturnId, ct) is { } done)
            return done.BillId == command.BillId
                ? Result.Success(ToResult(done))
                : Result.Failure<RecordedReturn>(ErrorCodes.ValidationFailed, "return id already used for another bill");

        if (await bills.GetAsync(command.BillId, ct) is not { } bill || bill.Status == BillStatus.Draft)
            return Result.Failure<RecordedReturn>(ErrorCodes.BillNotFound);
        if (bill.Status != BillStatus.Finalized) return Result.Failure<RecordedReturn>(ErrorCodes.ReturnBillNotFinalized);

        var earlier = await returns.ListByBillAsync(bill.Id, ct);
        var returnedBefore = Returned(earlier);
        var ret = new SalesReturn
        {
            Id = command.ReturnId, BillId = bill.Id, BillNumber = bill.Number, CustomerId = bill.CustomerId,
            IsTaxInvoice = bill.IsTaxInvoice, Method = command.Method, Reason = Blank(command.Reason),
        };

        // Several requests for one line (two taps) are one quantity.
        foreach (var request in command.Lines.Where(l => l.Qty.Milli > 0).GroupBy(l => l.BillLineId))
        {
            var line = bill.Lines.FirstOrDefault(l => l.Id == request.Key);
            if (line is null) return Result.Failure<RecordedReturn>(ErrorCodes.ValidationFailed, "line is not on this bill");
            var qty = new Qty(request.Sum(r => r.Qty.Milli));
            var before = returnedBefore.TryGetValue(line.Id, out var b) ? b : Qty.Zero;
            var left = line.Qty - before;
            if (qty > left)
                return Result.Failure<RecordedReturn>(Error.With(ErrorCodes.ReturnQtyTooLarge,
                    line.EnglishName.Length > 0 ? line.EnglishName : line.TeluguName, IndianFormat.Quantity(left)));
            ret.Lines.Add(new ReturnLine
            {
                BillLineId = line.Id, ItemId = line.ItemId, TeluguName = line.TeluguName, EnglishName = line.EnglishName,
                Unit = line.Unit, GstSlab = line.GstSlab, Qty = qty, Amount = ReturnCalculator.LineAmount(line, before, qty),
            });
        }

        ReturnCalculator.Compute(ret, earlier);

        // Refund: how the money goes back. Udhaar is reduced at most to zero; the rest is cash in hand.
        switch (command.Method)
        {
            case RefundMethod.Upi:
                ret.UpiRefund = ret.RefundTotal;
                ret.UpiRef = Blank(command.UpiRef);
                break;
            case RefundMethod.Dues:
                if (bill.CustomerId is not { } customerId) return Result.Failure<RecordedReturn>(ErrorCodes.ReturnNeedsCustomer);
                if (await customers.GetAsync(customerId, ct) is not { } customer) return Result.Failure<RecordedReturn>(ErrorCodes.CustomerNotFound);
                var owed = Math.Max(0, customer.DuesBalance.Value);
                ret.DuesCredit = new Paise(Math.Min(owed, ret.RefundTotal.Value));
                ret.CashRefund = ret.RefundTotal - ret.DuesCredit;
                break;
            default:
                ret.CashRefund = ret.RefundTotal;
                break;
        }

        var now = clock.Now;
        ret.Number = await returns.NextReturnNumberAsync(ct);
        ret.ReturnedAt = now;
        await returns.SaveAsync(ret, ct);

        // Stock comes back through the ledger (Item.Stock == Σ StockMoves), one move per item.
        foreach (var g in ret.Lines.GroupBy(l => l.ItemId))
            await ledger.AdjustAsync(g.Key, new Qty(g.Sum(l => l.Qty.Milli)), StockMoveReason.Return, bill.Id, ret.Number, ct);

        if (ret.DuesCredit.Value > 0)
        {
            // A Reversal against the bill: DuesAging cancels it from this bill's own charge first (DUES-1, amount-aware).
            await customers.AddDuesEntryAsync(new DuesEntry
            {
                CustomerId = bill.CustomerId!.Value, Kind = DuesEntryKind.Reversal, Amount = ret.DuesCredit, BillId = bill.Id, Note = ret.Number,
            }, ct);
            await outbox.AppendAsync("Customer", new DuesCreditedByReturn(bill.CustomerId.Value, now, ret.DuesCredit.Value, ret.Id), ct);
        }
        await outbox.AppendAsync("Return", new ReturnRecorded(ret.Id, now, bill.Id, ret.RefundTotal.Value,
            ret.CashRefund.Value, ret.UpiRefund.Value, ret.DuesCredit.Value), ct);
        return Result.Success(ToResult(ret));
    }

    internal static Dictionary<Guid, Qty> Returned(IEnumerable<SalesReturn> earlier) => earlier
        .SelectMany(r => r.Lines).GroupBy(l => l.BillLineId).ToDictionary(g => g.Key, g => new Qty(g.Sum(l => l.Qty.Milli)));

    private static RecordedReturn ToResult(SalesReturn r) => new(r.Id, r.Number, r.RefundTotal, r.CashRefund, r.UpiRefund, r.DuesCredit);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

internal sealed class GetBillReturnsHandler(IBillRepository bills, IReturnRepository returns) : IRequestHandler<GetBillReturnsQuery, BillReturns?>
{
    public async Task<BillReturns?> Handle(GetBillReturnsQuery q, CancellationToken ct)
    {
        if (await bills.GetAsync(q.BillId, ct) is not { } bill || bill.Status == BillStatus.Draft) return null;
        var made = await returns.ListByBillAsync(bill.Id, ct);
        var returned = RecordReturnHandler.Returned(made);
        return new BillReturns(bill,
            bill.Lines.Select(l =>
            {
                var back = returned.TryGetValue(l.Id, out var r) ? r : Qty.Zero;
                return new ReturnableLine(l, back, l.Qty - back);
            }).ToList(),
            made);
    }
}
