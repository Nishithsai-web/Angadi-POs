using Angadi.Application.Common;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;

namespace Angadi.Application.Billing;

// One record per use case (blueprint §3). Handlers return Result<T>; pipeline order is
// Validation → Logging → Transaction. The draft bill lives in a BillDraft held by the POS ViewModel.

/// <summary>
/// FR-13–15, FR-34. Handler: quota check → BillCalculator.Compute → number → save bill →
/// stock moves (Sale) → dues Charge if udhaar → outbox BillFinalized.
/// </summary>
public sealed record FinalizeBillCommand(Bill Draft) : ICommand<Result<FinalizedBill>>
{
    /// <summary>L-7 (owner decision, 2026-10-20): set by the UI when the shopkeeper explicitly confirms a
    /// bill whose computed total is ₹0 ("this bill is free"). Without it the validator rejects such a bill —
    /// a 100%-discount typo must not silently give goods away, burn a bill number and a quota slot.</summary>
    public bool FreeBillConfirmed { get; init; }
}
/// <summary>Saves the counter's unfinished bill after every change (an empty draft clears it).</summary>
public sealed record SaveBillDraftCommand(BillDraftSnapshot Draft) : ICommand<Result>;
/// <summary>The unfinished bill left when the app was last closed, or null.</summary>
public sealed record GetBillDraftQuery : IQuery<BillDraftSnapshot?>;

public sealed record FinalizedBill(Guid BillId, string Number, Paise GrandTotal, Paise ChangeDue);

/// <summary>FR-16. Restores stock (VoidRestore), reverses dues, outbox BillVoided.</summary>
public sealed record VoidBillCommand(Guid BillId, string Reason) : ICommand<Result>;

/// <summary>FR-17/19. Renders (or re-renders, FR-20) and opens the share sheet.</summary>
public sealed record ShareReceiptCommand(Guid BillId) : ICommand<Result>, INoTransaction;
/// <summary>Open WhatsApp to the bill's customer (their registered number) with the bill and their udhaar balance.</summary>
public sealed record SendBillToCustomerCommand(Guid BillId) : ICommand<Result>, INoTransaction;

/// <summary>FR-22.</summary>
public sealed record RecordDuesPaymentCommand(Guid CustomerId, Paise Amount, string? Note, string? UpiRef = null) : ICommand<Result>;

/// <summary>FR-23. Builds the Telugu reminder text and opens wa.me.</summary>
public sealed record SendDuesReminderCommand(Guid CustomerId, ReminderChannel Channel = ReminderChannel.WhatsApp) : ICommand<Result>, INoTransaction;
/// <summary>How a dues reminder reaches the customer's phone: WhatsApp, or SMS for customers without it.</summary>
public enum ReminderChannel { WhatsApp = 0, Sms = 1 }

/// <summary>FR-24. Purchase, damage, count correction.</summary>
public sealed record AdjustStockCommand(Guid ItemId, Qty Delta, StockMoveReason Reason, string? Note) : ICommand<Result>;

/// <summary>FR-34. Called before finalize and to show the free-bills meter.</summary>
public sealed record GetBillQuotaQuery : IQuery<BillQuota>;
public sealed record BillQuota(bool IsPro, int UsedThisMonth, int FreeLimit)
{
    public bool CanFinalize => IsPro || UsedThisMonth < FreeLimit;
}

/// <summary>
/// Builds a UPI intent URI for the tender QR and the receipt (FR-14/17).
/// Pure helper, implemented now so the format is fixed by contract.
/// </summary>
/// <summary>
/// The owner's own UPI QR (printed by their bank, PhonePe, Paytm, GPay…). Every shop's QR is unique: it names the
/// shop's payee address (pa), name (pn) and, for merchant accounts, the merchant code (mc).
/// </summary>
public sealed record UpiQr(string PayeeAddress, string? PayeeName, string? MerchantCode)
{
    public const int MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>Reads "upi://pay?pa=…" (any case, any parameter order). Null when the text is not a UPI payment QR.</summary>
    public static UpiQr? Parse(string? scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned)) return null;
        var text = scanned.Trim();
        if (!text.StartsWith("upi://pay", StringComparison.OrdinalIgnoreCase)) return null;
        var q = text.IndexOf('?', StringComparison.Ordinal);
        if (q < 0) return null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            values[part[..eq]] = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        if (!values.TryGetValue("pa", out var pa) || !System.Text.RegularExpressions.Regex.IsMatch(pa.Trim(), @"^[A-Za-z0-9.\-_]{2,256}@[A-Za-z0-9]{2,64}$"))
            return null;
        values.TryGetValue("pn", out var pn);
        values.TryGetValue("mc", out var mc);
        return new UpiQr(pa.Trim(), string.IsNullOrWhiteSpace(pn) ? null : pn.Trim(), string.IsNullOrWhiteSpace(mc) ? null : mc.Trim());
    }
}

public static class UpiLink
{
    /// <summary>
    /// The QR the customer scans at the counter: the shop's own payee (from their scanned QR when there is one,
    /// else the UPI ID typed at setup) with THIS bill's amount filled in, so the customer types nothing.
    /// </summary>
    public static string ForShop(Angadi.Domain.Shop.ShopProfile shop, Paise amount, string? billNumber = null)
    {
        if (UpiQr.Parse(shop.UpiQrPayload) is { } own)
        {
            var uri = Build(own.PayeeAddress, own.PayeeName ?? shop.Name, amount, billNumber);
            return own.MerchantCode is { } mc ? uri + "&mc=" + Uri.EscapeDataString(mc) : uri;
        }
        return Build(shop.UpiId, shop.Name, amount, billNumber);
    }

    public static string Build(string upiId, string payeeName, Paise amount, string? billNumber = null)
    {
        var rupees = (amount.Value / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        // pa stays unescaped: several UPI apps reject "%40" in the VPA.
        var uri = $"upi://pay?pa={upiId.Trim()}&pn={Uri.EscapeDataString(payeeName)}&am={rupees}&cu=INR";
        return billNumber is null ? uri : uri + $"&tn={Uri.EscapeDataString(billNumber)}";
    }
}
