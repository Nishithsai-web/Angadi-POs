using Angadi.Domain.Common;

namespace Angadi.Domain.Billing;

/// <summary>
/// The only place money math lives (FR-13). Pure, deterministic, ≥95% test coverage (NFR-10).
/// Rules to be written up by Agent D in docs/BILLING_RULES.md before implementing:
///   1. Line total = round(UnitPrice × Qty) − line discount. Prices are GST-inclusive.
///   2. Bill discount is apportioned across lines pro rata, so each slab's tax stays correct.
///   3. Per slab: Taxable = Gross × 10000 / (10000 + slabBp); Tax = Gross − Taxable.
///   4. GrandTotal rounds to the nearest whole rupee (half up); difference → RoundingAdjustment.
///   5. Bill of Supply (no GSTIN): TaxSplits empty, TaxTotal = 0, totals otherwise identical.
/// Implementation owner: Agent D. Contract owner: approved Day 1.
/// </summary>
public static class BillCalculator
{
    /// <summary>Fills every computed field on the bill and its lines. Does not change Status.</summary>
    public static void Compute(Bill bill, bool shopHasGstin)
        => throw new NotImplementedException("Agent D — Days 2–6");

    public static Paise ChangeDue(Bill bill)
        => bill.CashReceived - bill.GrandTotal;
}
