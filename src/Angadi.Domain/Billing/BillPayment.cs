using Angadi.Domain.Common;

namespace Angadi.Domain.Billing;

/// <summary>
/// v2 split tender: one part of how a bill was paid (docs/BILLING_RULES.md §Split tender). The parts of a bill sum
/// exactly to its GrandTotal; at most one cash part (the only one that can carry change) and at most one udhaar
/// part (only that amount goes to the customer's dues).
/// </summary>
public sealed class BillPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TenderType Tender { get; set; }
    public Paise Amount { get; set; }
    /// <summary>UPI parts only: the UTR / reference, optional.</summary>
    public string? UpiRef { get; set; }
    /// <summary>Cash part only: the note(s) handed over; zero = exact.</summary>
    public Paise CashReceived { get; set; }

    public Paise ChangeDue => Tender == TenderType.Cash && CashReceived > Amount ? CashReceived - Amount : Paise.Zero;
}

/// <summary>Reads any bill's payment as parts — the one way reports, receipts and dues look at tenders.</summary>
public static class BillPayments
{
    /// <summary>
    /// The stored parts; a bill saved before split tender existed (no parts) reads as ONE part of its single tender
    /// for the whole GrandTotal, so old bills report exactly as they always did.
    /// </summary>
    public static IReadOnlyList<BillPayment> PartsOf(Bill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (bill.Payments.Count > 0) return bill.Payments;
        if (bill.Tender is not { } tender || tender == TenderType.Split) return [];
        return
        [
            new BillPayment
            {
                Id = bill.Id, Tender = tender, Amount = bill.GrandTotal,
                UpiRef = tender == TenderType.Upi ? bill.UpiRef : null,
                CashReceived = tender == TenderType.Cash ? bill.CashReceived : Paise.Zero,
            },
        ];
    }

    /// <summary>How much of the bill was paid by <paramref name="tender"/> (0 when none).</summary>
    public static Paise AmountBy(Bill bill, TenderType tender) =>
        new(PartsOf(bill).Where(p => p.Tender == tender).Sum(p => p.Amount.Value));
}
