using Angadi.Domain.Common;

namespace Angadi.Domain.Billing;

/// <summary>
/// v2 split tender: one part of a bill's payment. Rules (docs/BILLING_RULES.md §Split tender): parts sum exactly to
/// the grand total; at most one cash part (change is computed on it), at most one udhaar part (needs a customer;
/// only that part goes to dues); UPI parts may carry a reference.
/// </summary>
public sealed class BillPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TenderType Tender { get; set; }
    public Paise Amount { get; set; }
    /// <summary>Cash part only: what the customer handed over (≥ Amount; 0 = exact).</summary>
    public Paise CashReceived { get; set; }
    /// <summary>UPI part only: UTR / reference, optional.</summary>
    public string? UpiRef { get; set; }
}

/// <summary>
/// The payment parts are the source of truth for "how was this bill paid". A single-tender bill (every v1 bill)
/// has no stored parts and is read as exactly one part, so old bills give identical totals.
/// </summary>
public static class BillPayments
{
    public static bool IsSplit(Bill bill) => bill.Payments.Count > 0;

    /// <summary>The bill's payment parts: the stored split, or one part made from Tender for a single-tender bill.</summary>
    public static IReadOnlyList<BillPayment> PartsOf(Bill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (bill.Payments.Count > 0) return bill.Payments;
        if (bill.Tender is not { } tender) return [];
        return
        [
            new BillPayment
            {
                Id = bill.Id, Tender = tender, Amount = bill.GrandTotal,
                CashReceived = tender == TenderType.Cash ? bill.CashReceived : Paise.Zero,
                UpiRef = tender == TenderType.Upi ? bill.UpiRef : null,
            },
        ];
    }

    /// <summary>How much of the bill was paid with <paramref name="tender"/>.</summary>
    public static Paise AmountBy(Bill bill, TenderType tender) =>
        new(PartsOf(bill).Where(p => p.Tender == tender).Sum(p => p.Amount.Value));

    /// <summary>Change to hand back: only ever on the cash part, never negative.</summary>
    public static Paise ChangeDue(Bill bill)
    {
        if (bill.Payments.Count == 0) return BillCalculator.ChangeDue(bill);
        var cash = bill.Payments.FirstOrDefault(p => p.Tender == TenderType.Cash);
        return cash is not null && cash.CashReceived > cash.Amount ? cash.CashReceived - cash.Amount : Paise.Zero;
    }

    /// <summary>The tender a split bill reports on old code paths: the largest part (the first one on a tie).</summary>
    public static TenderType MainTender(IReadOnlyList<BillPayment> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("No payment parts.", nameof(parts));
        var main = parts[0];
        foreach (var p in parts.Skip(1))
            if (p.Amount > main.Amount) main = p;
        return main.Tender;
    }
}
