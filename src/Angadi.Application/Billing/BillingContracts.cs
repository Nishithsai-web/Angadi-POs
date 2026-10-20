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
public sealed record FinalizeBillCommand(Bill Draft) : ICommand<Result<FinalizedBill>>;
public sealed record FinalizedBill(Guid BillId, string Number, Paise GrandTotal, Paise ChangeDue);

/// <summary>FR-16. Restores stock (VoidRestore), reverses dues, outbox BillVoided.</summary>
public sealed record VoidBillCommand(Guid BillId, string Reason) : ICommand<Result>;

/// <summary>FR-17/19. Renders (or re-renders, FR-20) and opens the share sheet.</summary>
public sealed record ShareReceiptCommand(Guid BillId) : ICommand<Result>, INoTransaction;

/// <summary>FR-22.</summary>
public sealed record RecordDuesPaymentCommand(Guid CustomerId, Paise Amount, string? Note, string? UpiRef = null) : ICommand<Result>;

/// <summary>FR-23. Builds the Telugu reminder text and opens wa.me.</summary>
public sealed record SendDuesReminderCommand(Guid CustomerId) : ICommand<Result>, INoTransaction;

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
public static class UpiLink
{
    public static string Build(string upiId, string payeeName, Paise amount, string? billNumber = null)
    {
        var rupees = (amount.Value / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        // pa stays unescaped: several UPI apps reject "%40" in the VPA.
        var uri = $"upi://pay?pa={upiId.Trim()}&pn={Uri.EscapeDataString(payeeName)}&am={rupees}&cu=INR";
        return billNumber is null ? uri : uri + $"&tn={Uri.EscapeDataString(billNumber)}";
    }
}
