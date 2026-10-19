using System.Text.RegularExpressions;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Events;
using Angadi.Domain.Inventory;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Billing;

// ---------- Validators: cheap, synchronous checks before any I/O ----------

internal sealed class FinalizeBillValidator : IValidator<FinalizeBillCommand>
{
    public Error? Validate(FinalizeBillCommand c)
    {
        var bill = c.Draft;
        if (bill.Lines.Count == 0) return new Error(ErrorCodes.BillEmpty);
        if (bill.Lines.Any(l => l.Qty.Milli <= 0)) return new Error(ErrorCodes.BillQtyInvalid);
        if (bill.Lines.Any(l => l.UnitPrice.Value < 0)) return new Error(ErrorCodes.ValidationFailed, "negative price");
        if (bill.Tender is null) return new Error(ErrorCodes.BillTenderMissing);
        if (bill.Tender == TenderType.Udhaar && bill.CustomerId is null) return new Error(ErrorCodes.BillUdhaarNeedsCustomer);
        if (bill.Status != BillStatus.Draft) return new Error(ErrorCodes.ValidationFailed, "bill already finalized");
        return null;
    }
}

internal sealed class VoidBillValidator : IValidator<VoidBillCommand>
{
    public Error? Validate(VoidBillCommand c) =>
        string.IsNullOrWhiteSpace(c.Reason) ? new Error(ErrorCodes.ValidationFailed, "void reason required") : null;
}

internal sealed class RecordDuesPaymentValidator : IValidator<RecordDuesPaymentCommand>
{
    public Error? Validate(RecordDuesPaymentCommand c) =>
        c.Amount.Value <= 0 ? new Error(ErrorCodes.ValidationFailed, "payment must be positive") : null;
}

internal sealed class AdjustStockValidator : IValidator<AdjustStockCommand>
{
    public Error? Validate(AdjustStockCommand c) =>
        c.Delta.Milli == 0 ? new Error(ErrorCodes.BillQtyInvalid)
        : c.Reason is StockMoveReason.Sale or StockMoveReason.VoidRestore ? new Error(ErrorCodes.ValidationFailed, "sales move stock only via bills")
        : null;
}

// ---------- Handlers ----------

/// <summary>FR-13/14/15/34. Runs inside one transaction (Sender); any failure rolls everything back.</summary>
internal sealed class FinalizeBillHandler(
    IBillRepository bills,
    IShopProfileRepository shop,
    ICustomerRepository customers,
    IItemRepository items,
    IStockLedger ledger,
    IOutbox outbox,
    ISubscriptionService subscription,
    IClock clock,
    IOptions<BillingOptions> options) : IRequestHandler<FinalizeBillCommand, Result<FinalizedBill>>
{
    public async Task<Result<FinalizedBill>> Handle(FinalizeBillCommand command, CancellationToken ct)
    {
        var bill = command.Draft;
        var now = clock.Now;

        // FR-34 free-tier quota.
        var status = await subscription.GetStatusAsync(ct);
        if (!status.IsPro && await bills.CountFinalizedInMonthAsync(now.Year, now.Month, ct) >= options.Value.FreeBillsPerMonth)
            return Result.Failure<FinalizedBill>(ErrorCodes.FreeLimitReached);

        var profile = await shop.GetAsync(ct);
        if (profile is null) return Result.Failure<FinalizedBill>(ErrorCodes.ValidationFailed, "shop profile missing");

        foreach (var itemId in bill.Lines.Select(l => l.ItemId).Distinct())
            if (await items.GetAsync(itemId, ct) is null)
                return Result.Failure<FinalizedBill>(ErrorCodes.ItemNotFound, itemId.ToString("D", System.Globalization.CultureInfo.InvariantCulture));

        BillCalculator.Compute(bill, shopHasGstin: !string.IsNullOrWhiteSpace(profile.Gstin));

        var cashTyped = bill.CashReceived;
        switch (bill.Tender)
        {
            case TenderType.Cash:
                if (bill.CashReceived < bill.GrandTotal && bill.CashReceived.Value != 0)
                    return Result.Failure<FinalizedBill>(ErrorCodes.BillCashShort);
                if (bill.CashReceived.Value == 0) bill.CashReceived = bill.GrandTotal;   // exact cash, nothing typed
                break;
            case TenderType.Udhaar:
                if (await customers.GetAsync(bill.CustomerId!.Value, ct) is null)
                    return Result.Failure<FinalizedBill>(ErrorCodes.CustomerNotFound);
                break;
        }

        try
        {
            bill.Number = await shop.NextBillNumberAsync(ct);
            bill.Status = BillStatus.Finalized;
            bill.FinalizedAt = now;
            await bills.SaveAsync(bill, ct);

            // Stock may go negative (sold before stock-in was recorded); low-stock alerts surface it.
            foreach (var g in bill.Lines.GroupBy(l => l.ItemId))
                await ledger.AdjustAsync(g.Key, new Qty(-g.Sum(l => l.Qty.Milli)), StockMoveReason.Sale, bill.Id, ct: ct);

            if (bill.Tender == TenderType.Udhaar && bill.GrandTotal.Value > 0)
                await customers.AddDuesEntryAsync(new DuesEntry
                {
                    CustomerId = bill.CustomerId!.Value, Kind = DuesEntryKind.Charge, Amount = bill.GrandTotal, BillId = bill.Id,
                }, ct);

            await outbox.AppendAsync("Bill", new BillFinalized(bill.Id, now, bill.GrandTotal.Value), ct);
        }
        catch
        {
            // The transaction rolls back; put the in-memory draft back too so the shopkeeper can retry.
            bill.Number = "";
            bill.Status = BillStatus.Draft;
            bill.FinalizedAt = null;
            bill.CashReceived = cashTyped;   // the auto-filled "exact cash" must not survive into a retry
            throw;
        }

        // Change is only ever due on cash; for UPI/udhaar ChangeDue(bill) would be −GrandTotal.
        var change = bill.Tender == TenderType.Cash ? BillCalculator.ChangeDue(bill) : Paise.Zero;
        return Result.Success(new FinalizedBill(bill.Id, bill.Number, bill.GrandTotal, change));
    }
}

/// <summary>FR-16. Stock back, dues reversed, bill kept and marked VOID.</summary>
internal sealed class VoidBillHandler(
    IBillRepository bills, ICustomerRepository customers, IStockLedger ledger, IOutbox outbox, IClock clock)
    : IRequestHandler<VoidBillCommand, Result>
{
    public async Task<Result> Handle(VoidBillCommand command, CancellationToken ct)
    {
        var bill = await bills.GetAsync(command.BillId, ct);
        if (bill is null) return Result.Failure(ErrorCodes.BillNotFound);
        if (bill.Status == BillStatus.Void) return Result.Failure(ErrorCodes.BillAlreadyVoid);
        if (bill.Status != BillStatus.Finalized) return Result.Failure(ErrorCodes.ValidationFailed, "only finalized bills can be voided");

        var now = clock.Now;
        bill.Status = BillStatus.Void;
        bill.VoidedAt = now;
        bill.VoidReason = command.Reason.Trim();
        await bills.SaveAsync(bill, ct);

        foreach (var g in bill.Lines.GroupBy(l => l.ItemId))
            await ledger.AdjustAsync(g.Key, new Qty(g.Sum(l => l.Qty.Milli)), StockMoveReason.VoidRestore, bill.Id, ct: ct);

        if (bill.Tender == TenderType.Udhaar && bill.CustomerId is { } customerId && bill.GrandTotal.Value > 0)
            await customers.AddDuesEntryAsync(new DuesEntry
            {
                CustomerId = customerId, Kind = DuesEntryKind.Reversal, Amount = bill.GrandTotal, BillId = bill.Id, Note = bill.VoidReason,
            }, ct);

        await outbox.AppendAsync("Bill", new BillVoided(bill.Id, now, bill.VoidReason), ct);
        return Result.Success();
    }
}

/// <summary>FR-17/19/20. No transaction: the share sheet waits on the user.</summary>
internal sealed class ShareReceiptHandler(
    IBillRepository bills, IShopProfileRepository shop,
    IReceiptRenderer renderer, IReceiptShare share, ILocalizer text)
    : IRequestHandler<ShareReceiptCommand, Result>
{
    public async Task<Result> Handle(ShareReceiptCommand command, CancellationToken ct)
    {
        var bill = await bills.GetAsync(command.BillId, ct);
        if (bill is null) return Result.Failure(ErrorCodes.BillNotFound);
        var profile = await shop.GetAsync(ct);
        if (profile is null) return Result.Failure(ErrorCodes.ValidationFailed, "shop profile missing");

        var pdf = await renderer.RenderAsync(bill, profile, ct);
        if (pdf.IsFailure) return Result.Failure(pdf.Error);

        var title = text.Format("receipt.share.text", profile.Name, bill.Number, IndianFormat.Rupees(bill.GrandTotal));
        return await share.ShareFileAsync(pdf.Value, title, ct);
    }
}

internal sealed class RecordDuesPaymentHandler(ICustomerRepository customers, IOutbox outbox, IClock clock)
    : IRequestHandler<RecordDuesPaymentCommand, Result>
{
    public async Task<Result> Handle(RecordDuesPaymentCommand command, CancellationToken ct)
    {
        var customer = await customers.GetAsync(command.CustomerId, ct);
        if (customer is null) return Result.Failure(ErrorCodes.CustomerNotFound);
        if (command.Amount > customer.DuesBalance)
            return Result.Failure(ErrorCodes.ValidationFailed, "payment exceeds dues");

        await customers.AddDuesEntryAsync(new DuesEntry
        {
            CustomerId = customer.Id, Kind = DuesEntryKind.Payment, Amount = command.Amount, Note = command.Note?.Trim(),
        }, ct);
        await outbox.AppendAsync("Customer", new DuesPaymentRecorded(customer.Id, clock.Now, command.Amount.Value), ct);
        return Result.Success();
    }
}

/// <summary>FR-23. Prefilled Telugu WhatsApp message via wa.me (ADR-4). No transaction.</summary>
internal sealed class SendDuesReminderHandler(
    ICustomerRepository customers, IShopProfileRepository shop, IReceiptShare share, ILocalizer text)
    : IRequestHandler<SendDuesReminderCommand, Result>
{
    public async Task<Result> Handle(SendDuesReminderCommand command, CancellationToken ct)
    {
        var customer = await customers.GetAsync(command.CustomerId, ct);
        if (customer is null) return Result.Failure(ErrorCodes.CustomerNotFound);
        if (string.IsNullOrWhiteSpace(customer.Phone)) return Result.Failure(ErrorCodes.ValidationFailed, "customer has no phone");
        if (customer.DuesBalance.Value <= 0) return Result.Failure(ErrorCodes.ValidationFailed, "no dues");

        var profile = await shop.GetAsync(ct);
        var message = text.Format("customers.remind.message", customer.Name, profile?.Name ?? "", IndianFormat.Rupees(customer.DuesBalance));
        return await share.OpenWhatsAppAsync(customer.Phone, message, ct);
    }
}

internal sealed class AdjustStockHandler(IItemRepository items, IStockLedger ledger, IOutbox outbox, IClock clock)
    : IRequestHandler<AdjustStockCommand, Result>
{
    public async Task<Result> Handle(AdjustStockCommand command, CancellationToken ct)
    {
        if (await items.GetAsync(command.ItemId, ct) is null) return Result.Failure(ErrorCodes.ItemNotFound);
        await ledger.AdjustAsync(command.ItemId, command.Delta, command.Reason, note: command.Note?.Trim(), ct: ct);
        await outbox.AppendAsync("Item", new StockAdjusted(command.ItemId, clock.Now, command.Delta.Milli, (int)command.Reason), ct);
        return Result.Success();
    }
}

internal sealed class GetBillQuotaHandler(IBillRepository bills, ISubscriptionService subscription, IClock clock, IOptions<BillingOptions> options)
    : IRequestHandler<GetBillQuotaQuery, BillQuota>
{
    public async Task<BillQuota> Handle(GetBillQuotaQuery query, CancellationToken ct)
    {
        var now = clock.Now;
        var status = await subscription.GetStatusAsync(ct);
        var used = await bills.CountFinalizedInMonthAsync(now.Year, now.Month, ct);
        return new BillQuota(status.IsPro, used, options.Value.FreeBillsPerMonth);
    }
}

/// <summary>Indian mobile numbers to the E.164-without-plus form wa.me wants (919876543210).</summary>
public static partial class PhoneNumbers
{
    [GeneratedRegex("[^0-9]")]
    private static partial Regex NonDigits();

    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = NonDigits().Replace(input, "");
        if (digits.StartsWith('0')) digits = digits.TrimStart('0');
        return digits.Length switch
        {
            10 when digits[0] is >= '6' and <= '9' => "91" + digits,
            // +91 followed by a mobile (6–9); "+91 40 2345 6789" is a landline and wa.me can't reach it.
            12 when digits.StartsWith("91", StringComparison.Ordinal) && digits[2] is >= '6' and <= '9' => digits,
            _ => null,
        };
    }
}
