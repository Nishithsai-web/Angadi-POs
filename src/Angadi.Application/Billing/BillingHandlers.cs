using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Events;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Billing;

// ---------- Validators: cheap, synchronous checks before any I/O ----------

internal sealed class FinalizeBillValidator : IValidator<FinalizeBillCommand>
{
    internal const long MaxLineQtyMilli = 100_000_000;   // 1,00,000 whole units

    private static bool IsOver100Percent(Discount d) => d.Kind == DiscountKind.PercentBasisPoints && d.Amount > 10_000;

    public Error? Validate(FinalizeBillCommand c)
    {
        var bill = c.Draft;
        if (bill.Lines.Count == 0) return new Error(ErrorCodes.BillEmpty);
        // M7: 1,00,000 units per line is far past any counter sale; beyond it is a typo, and it bounds the maths.
        if (bill.Lines.Any(l => l.Qty.Milli <= 0 || l.Qty.Milli > MaxLineQtyMilli)) return new Error(ErrorCodes.BillQtyInvalid);
        if (bill.Lines.Any(l => l.UnitPrice.Value < 0)) return new Error(ErrorCodes.ValidationFailed, "negative price");
        if (bill.Tender is null || !Enum.IsDefined(bill.Tender.Value)) return new Error(ErrorCodes.BillTenderMissing);   // LOW: no cast-in tenders
        if (bill.Tender == TenderType.Udhaar && bill.CustomerId is null) return new Error(ErrorCodes.BillUdhaarNeedsCustomer);
        // Status is checked in the handler, after the idempotency check: a retry of a bill that already saved is not an error.
        // B1: only a cash tender may carry cash received.
        if (bill.Tender != TenderType.Cash && bill.CashReceived.Value != 0) return new Error(ErrorCodes.ValidationFailed, "cash received on a non-cash tender");
        if (bill.Tender != TenderType.Upi && !string.IsNullOrWhiteSpace(bill.UpiRef)) return new Error(ErrorCodes.ValidationFailed, "UPI reference on a non-UPI tender");
        // M9: a typo like 150% must stop the bill, never silently clamp to free goods.
        if (IsOver100Percent(bill.BillDiscount) || bill.Lines.Any(l => IsOver100Percent(l.LineDiscount)))
            return new Error(ErrorCodes.BillDiscountTooLarge);
        // H8: GST 2.0 abolished 12%/28% (22 Sep 2025); fail closed rather than bill wrong tax.
        var legacy = bill.Lines.FirstOrDefault(l => GstSlabs.IsLegacy(l.GstSlab));
        if (legacy is not null) return new Error(ErrorCodes.BillLegacySlab, legacy.EnglishName);
        return null;
    }
}

internal sealed class VoidBillValidator : IValidator<VoidBillCommand>
{
    public Error? Validate(VoidBillCommand c) =>
        string.IsNullOrWhiteSpace(c.Reason) ? new Error(ErrorCodes.VoidReasonRequired) : null;
}

internal sealed class RecordDuesPaymentValidator : IValidator<RecordDuesPaymentCommand>
{
    public Error? Validate(RecordDuesPaymentCommand c) =>
        c.Amount.Value <= 0 ? new Error(ErrorCodes.AmountInvalid) : null;
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
    IQuotaStore quota,
    IBillDraftStore drafts,
    IClock clock,
    IOptions<BillingOptions> options) : IRequestHandler<FinalizeBillCommand, Result<FinalizedBill>>
{
    public async Task<Result<FinalizedBill>> Handle(FinalizeBillCommand command, CancellationToken ct)
    {
        var bill = command.Draft;
        var now = clock.Now;

        // Idempotent: a retry of a bill that already saved (the watchdog fired while it was still committing, or a
        // double tap) returns that bill. Never a second bill number, never stock or dues taken twice.
        if (await bills.GetAsync(bill.Id, ct) is { Status: not BillStatus.Draft } saved)
        {
            await drafts.ClearAsync(ct);
            return Result.Success(new FinalizedBill(saved.Id, saved.Number, saved.GrandTotal,
                saved.Tender == TenderType.Cash ? BillCalculator.ChangeDue(saved) : Paise.Zero));
        }
        if (bill.Status != BillStatus.Draft) return Result.Failure<FinalizedBill>(ErrorCodes.ValidationFailed, "bill already finalized");

        // FR-34 free-tier quota. Defence in depth: a status that doesn't answer at once counts as free (fail closed),
        // so billing can never hang on the subscription service while this transaction holds the database.
        var status = await SubscriptionGuard.StatusAsync(subscription, ct);
        if (!status.IsPro && await quota.UsedThisMonthAsync(now, ct) >= options.Value.FreeBillsPerMonth)
            return Result.Failure<FinalizedBill>(Error.With(ErrorCodes.FreeLimitReached,
                options.Value.FreeBillsPerMonth, options.Value.MonthlyPriceRupees));

        var profile = await shop.GetAsync(ct);
        if (profile is null) return Result.Failure<FinalizedBill>(ErrorCodes.ValidationFailed, "shop profile missing");

        foreach (var itemId in bill.Lines.Select(l => l.ItemId).Distinct())
            if (await items.GetAsync(itemId, ct) is null)
                return Result.Failure<FinalizedBill>(ErrorCodes.ItemNotFound, itemId.ToString("D", System.Globalization.CultureInfo.InvariantCulture));

        try { BillCalculator.Compute(bill, shopHasGstin: !string.IsNullOrWhiteSpace(profile.Gstin)); }
        catch (OverflowException) { return Result.Failure<FinalizedBill>(ErrorCodes.BillAmountTooLarge); }   // M7: checked maths

        var cashTyped = bill.CashReceived;
        switch (bill.Tender)
        {
            case TenderType.Cash:
                if (bill.CashReceived < bill.GrandTotal && bill.CashReceived.Value != 0)
                    return Result.Failure<FinalizedBill>(Error.With(ErrorCodes.BillCashShort,
                        IndianFormat.Rupees(new Paise(bill.GrandTotal.Value - bill.CashReceived.Value))));
                if (bill.CashReceived.Value == 0) bill.CashReceived = bill.GrandTotal;   // exact cash, nothing typed
                break;
        }
        // Any tender may carry a customer (account history); it must exist. Udhaar requires one (validator).
        if (bill.CustomerId is { } attached && await customers.GetAsync(attached, ct) is null)
            return Result.Failure<FinalizedBill>(ErrorCodes.CustomerNotFound);

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

            if (bill.Tender == TenderType.Udhaar && bill.GrandTotal.Value > 0)   // M6: sync seam sees the dues side too
                await outbox.AppendAsync("Customer", new DuesCharged(bill.CustomerId!.Value, now, bill.GrandTotal.Value, bill.Id), ct);
            await outbox.AppendAsync("Bill", new BillFinalized(bill.Id, now, bill.GrandTotal.Value), ct);
            await quota.IncrementAsync(now, ct);   // B2: same transaction as the check above
            await drafts.ClearAsync(ct);           // same transaction: a paid bill's draft can never come back
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

        // H1 (approved as specified): if any part of an udhaar bill has been paid, a full reversal would push dues
        // negative and lock the account. Fail closed before mutating anything; real refunds are v2.
        if (bill.Tender == TenderType.Udhaar && bill.CustomerId is { } owingId && bill.GrandTotal.Value > 0)
        {
            var owing = await customers.GetAsync(owingId, ct);
            if (owing is null) return Result.Failure(ErrorCodes.CustomerNotFound);
            if (owing.DuesBalance < bill.GrandTotal)
                return Result.Failure(ErrorCodes.BillVoidBlockedByPayments,
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"dues {owing.DuesBalance.Value} < bill {bill.GrandTotal.Value}"));
        }

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

/// <summary>
/// After a bill for a customer with a phone: the bill summary and their udhaar balance, to THEIR registered number.
/// </summary>
internal sealed class SendBillToCustomerHandler(
    IBillRepository bills, ICustomerRepository customers, IShopProfileRepository shop, IReceiptShare share, ILocalizer text)
    : IRequestHandler<SendBillToCustomerCommand, Result>
{
    public async Task<Result> Handle(SendBillToCustomerCommand command, CancellationToken ct)
    {
        if (await bills.GetAsync(command.BillId, ct) is not { } bill) return Result.Failure(ErrorCodes.BillNotFound);
        if (bill.CustomerId is not { } customerId || await customers.GetAsync(customerId, ct) is not { } customer)
            return Result.Failure(ErrorCodes.CustomerNotFound);
        if (string.IsNullOrWhiteSpace(customer.Phone)) return Result.Failure(ErrorCodes.CustomerNoPhone);

        var profile = await shop.GetAsync(ct);
        var tender = bill.Tender switch
        {
            TenderType.Cash => text["tender.cash"],
            TenderType.Upi => text["tender.upi"],
            _ => text["tender.udhaar"],
        };
        var items = string.Join("\n", bill.Lines.Take(15).Select(l =>
            $"• {text.Name(l)} {IndianFormat.Quantity(l.Qty)} {text.UnitLabel(l.Unit)} — {IndianFormat.Rupees(l.LineTotal)}"));
        var message = text.Format("receipt.customer.message", customer.Name, profile?.Name ?? "", bill.Number,
            IndianFormat.Rupees(bill.GrandTotal), tender) + "\n" + items;
        if (customer.DuesBalance.Value > 0)
            message += "\n" + text.Format("receipt.customer.balance", IndianFormat.Rupees(customer.DuesBalance));
        return await share.OpenWhatsAppAsync(customer.Phone, message, ct);
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
            return Result.Failure(Error.With(ErrorCodes.DuesExceedsBalance, IndianFormat.Rupees(customer.DuesBalance)));

        await customers.AddDuesEntryAsync(new DuesEntry
        {
            CustomerId = customer.Id, Kind = DuesEntryKind.Payment, Amount = command.Amount, Note = command.Note?.Trim(), UpiRef = command.UpiRef?.Trim(),
        }, ct);
        await outbox.AppendAsync("Customer", new DuesPaymentRecorded(customer.Id, clock.Now, command.Amount.Value), ct);
        return Result.Success();
    }
}

/// <summary>FR-23. Prefilled Telugu WhatsApp message via wa.me (ADR-4). No transaction.</summary>
internal sealed class SendDuesReminderHandler(
    ICustomerRepository customers, IShopProfileRepository shop, IReceiptShare share, ILocalizer text, IClock clock)
    : IRequestHandler<SendDuesReminderCommand, Result>
{
    public async Task<Result> Handle(SendDuesReminderCommand command, CancellationToken ct)
    {
        var customer = await customers.GetAsync(command.CustomerId, ct);
        if (customer is null) return Result.Failure(ErrorCodes.CustomerNotFound);
        if (string.IsNullOrWhiteSpace(customer.Phone)) return Result.Failure(ErrorCodes.CustomerNoPhone);
        if (customer.DuesBalance.Value <= 0) return Result.Failure(ErrorCodes.CustomerNoDues);

        var profile = await shop.GetAsync(ct);
        var message = text.Format("customers.remind.message", customer.Name, profile?.Name ?? "", IndianFormat.Rupees(customer.DuesBalance));
        // The udhaar itself: which days, how much is still open (oldest first, at most 5 lines).
        var open = Angadi.Application.Customers.DuesAging.UnpaidCharges(await customers.GetLedgerAsync(customer.Id, ct));
        if (open.Count > 0)
        {
            var lines = open.Take(5).Select(o => "• " + text.Short(o.At) + " — " + IndianFormat.Rupees(o.Remaining));
            message += "\n" + text["customers.remind.bills"] + "\n" + string.Join("\n", lines);
            if (open.Count > 5) message += "\n• …";
        }
        if (customer.PromisedPayDate is { } promised)
            message += "\n" + text.Format("customers.remind.promised", text.Short(promised));

        var sent = command.Channel == ReminderChannel.Sms
            ? await share.OpenSmsAsync(customer.Phone, message, ct)
            : await share.OpenWhatsAppAsync(customer.Phone, message, ct);
        if (sent.IsFailure) return sent;

        customer.LastRemindedAt = clock.Now;   // keeps them off "Due for reminder" for a few days
        await customers.SaveAsync(customer, ct);
        return sent;
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

internal sealed class GetBillQuotaHandler(IQuotaStore quota, ISubscriptionService subscription, IClock clock, IOptions<BillingOptions> options)
    : IRequestHandler<GetBillQuotaQuery, BillQuota>
{
    public async Task<BillQuota> Handle(GetBillQuotaQuery query, CancellationToken ct)
    {
        var now = clock.Now;
        var status = await SubscriptionGuard.StatusAsync(subscription, ct);
        var used = await quota.UsedThisMonthAsync(now, ct);
        return new BillQuota(status.IsPro, used, options.Value.FreeBillsPerMonth, status.NeedsReconfirm);
    }
}

/// <summary>Bounded, fail-closed read of the subscription status for use inside transactions.</summary>
internal static class SubscriptionGuard
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly SubscriptionStatus Free = new(false, null, null);

    public static async Task<SubscriptionStatus> StatusAsync(ISubscriptionService subscription, CancellationToken ct)
    {
        try { return await subscription.GetStatusAsync(ct).WaitAsync(Timeout, ct); }
        catch (TimeoutException) { return Free; }
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

/// <summary>Keeps the counter's unfinished bill across the app being closed (phone call, Android memory, crash).</summary>
internal sealed class BillDraftHandlers(IBillDraftStore store, ILogger<BillDraftHandlers> log)
    : IRequestHandler<SaveBillDraftCommand, Result>, IRequestHandler<GetBillDraftQuery, BillDraftSnapshot?>
{
    public async Task<Result> Handle(SaveBillDraftCommand command, CancellationToken ct)
    {
        // An empty draft, or one whose bill already saved (a late write racing finalize), is cleared instead.
        if (command.Draft.IsEmpty || await store.BillExistsAsync(command.Draft.BillId, ct))
            await store.ClearAsync(ct);
        else
            await store.SaveAsync(JsonSerializer.Serialize(command.Draft, DraftJson.Default.BillDraftSnapshot), ct);
        return Result.Success();
    }

    public async Task<BillDraftSnapshot?> Handle(GetBillDraftQuery query, CancellationToken ct)
    {
        var json = await store.LoadAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        BillDraftSnapshot? draft;
        try { draft = JsonSerializer.Deserialize(json, DraftJson.Default.BillDraftSnapshot); }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Unreadable saved draft discarded");
            await store.ClearAsync(ct);
            return null;
        }
        if (draft is null || draft.Lines is null || draft.IsEmpty || await store.BillExistsAsync(draft.BillId, ct)) return null;
        return draft;
    }
}

[JsonSerializable(typeof(BillDraftSnapshot))]
internal sealed partial class DraftJson : JsonSerializerContext;
