using Angadi.Application.Customers;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;

namespace Angadi.Tests.Application;

/// <summary>
/// DuesAging settlement semantics, pinned by the T3 data-layer audit (T3-06, T3-07) and DUES-1:
/// a reversal cancels min(its amount, its own charge's remaining) — never the whole charge blindly —
/// and any excess joins the payment pool. Payments/write-offs settle the rest oldest first.
/// </summary>
public class DuesAgingTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly Guid BillA = Guid.NewGuid();
    private static readonly Guid BillB = Guid.NewGuid();

    private static DuesEntry E(DuesEntryKind kind, long paise, int day, Guid? billId = null) => new()
    {
        Kind = kind, Amount = new Paise(paise), BillId = billId, CreatedAt = Day0.AddDays(day),
    };

    private static List<DuesEntry> Ledger(params DuesEntry[] entries) => [.. entries];

    // T3-06: a partial payment leaves the remainder on the OLDEST charge; the newer charge is untouched.
    [Fact]
    public void T3_06_PartialPayment_LeavesRemainderOnTheOldestCharge()
    {
        var open = DuesAging.UnpaidCharges(Ledger(
            E(DuesEntryKind.Charge, 6000, 0, BillA),        // ₹60, day 0
            E(DuesEntryKind.Charge, 6000, 5, BillB),        // ₹60, day 5
            E(DuesEntryKind.Payment, 4000, 6)));            // ₹40 paid

        Assert.Equal(2, open.Count);
        Assert.Equal(BillA, open[0].BillId);
        Assert.Equal(2000, open[0].Remaining.Value);        // ₹20 left of the oldest
        Assert.Equal(BillB, open[1].BillId);
        Assert.Equal(6000, open[1].Remaining.Value);        // the newer charge is whole
        Assert.Equal(10, DuesAging.OldestUnpaidDays(Ledger(
            E(DuesEntryKind.Charge, 6000, 0, BillA),
            E(DuesEntryKind.Charge, 6000, 5, BillB),
            E(DuesEntryKind.Payment, 4000, 6)), new DateOnly(2026, 10, 11)));
    }

    // T3-07a: a reversal cancels ONLY its own charge — the older, unrelated charge stays fully open,
    // and a payment then redistributes across what remains.
    [Fact]
    public void T3_07_ReversalCancelsOnlyItsOwnCharge_PaymentsRedistribute()
    {
        var open = DuesAging.UnpaidCharges(Ledger(
            E(DuesEntryKind.Charge, 6000, 0, BillA),
            E(DuesEntryKind.Charge, 6000, 5, BillB),
            E(DuesEntryKind.Reversal, 6000, 6, BillB),       // bill B voided in full
            E(DuesEntryKind.Payment, 2500, 6)));            // ₹25 paid — must land on A, the only open charge

        var only = Assert.Single(open);
        Assert.Equal(BillA, only.BillId);
        Assert.Equal(3500, only.Remaining.Value);
    }

    // DUES-1: a PARTIAL reversal cancels only its amount from its own charge (the old code cancelled
    // the whole charge because it never read the amount).
    [Fact]
    public void PartialReversal_CancelsOnlyItsAmount_FromItsOwnCharge()
    {
        var open = DuesAging.UnpaidCharges(Ledger(
            E(DuesEntryKind.Charge, 6000, 0, BillA),
            E(DuesEntryKind.Charge, 6000, 5, BillB),
            E(DuesEntryKind.Reversal, 2500, 6, BillA)));    // ₹25 of bill A reversed

        Assert.Equal(2, open.Count);
        Assert.Equal(3500, open[0].Remaining.Value);        // A keeps the other ₹35
        Assert.Equal(6000, open[1].Remaining.Value);        // B untouched
    }

    // DUES-1: reversal excess (more than its charge — defensive; H1 normally prevents it) joins the
    // payment pool and settles other charges instead of vanishing.
    [Fact]
    public void ReversalExcess_JoinsThePool_AndSettlesOtherCharges()
    {
        var open = DuesAging.UnpaidCharges(Ledger(
            E(DuesEntryKind.Charge, 6000, 0, BillA),
            E(DuesEntryKind.Charge, 6000, 5, BillB),
            E(DuesEntryKind.Reversal, 10_000, 6, BillA)));   // ₹100 reversed against a ₹60 charge

        var only = Assert.Single(open);
        Assert.Equal(BillB, only.BillId);
        Assert.Equal(2000, only.Remaining.Value);           // ₹40 of excess settled B down to ₹20
    }

    // Regression: today's only real flow — void = full reversal of the bill — behaves exactly as before.
    [Fact]
    public void FullReversal_OfTheOnlyCharge_LeavesNothingOpen()
    {
        var ledger = Ledger(
            E(DuesEntryKind.Charge, 6000, 0, BillA),
            E(DuesEntryKind.Reversal, 6000, 1, BillA));

        Assert.Empty(DuesAging.UnpaidCharges(ledger));
        Assert.Null(DuesAging.OldestUnpaidDays(ledger, new DateOnly(2026, 10, 11)));
    }

    // A bill-less reversal (manual correction) settles oldest first, like a payment.
    [Fact]
    public void BillLessReversal_SettlesOldestFirst()
    {
        var open = DuesAging.UnpaidCharges(Ledger(
            E(DuesEntryKind.Charge, 6000, 0, BillA),
            E(DuesEntryKind.Charge, 6000, 5, BillB),
            E(DuesEntryKind.Reversal, 6000, 6)));

        var only = Assert.Single(open);
        Assert.Equal(BillB, only.BillId);
        Assert.Equal(6000, only.Remaining.Value);
    }
}
