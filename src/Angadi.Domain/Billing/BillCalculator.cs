using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Domain.Billing;

/// <summary>
/// The only place money math lives (FR-13). Pure and deterministic; rules in docs/BILLING_RULES.md.
///   1. Gross = round(UnitPrice × Qty). Prices are GST-inclusive.
///   2. Line discount (% or flat, capped at gross) → LineTotal.
///   3. Bill discount (% or flat, capped at subtotal) is apportioned across lines pro rata by
///      LineTotal using largest remainder, so shares sum exactly and each slab's tax stays right.
///   4. Per slab: Taxable = round(Net × 10000 / (10000 + slabBp)); Tax = Net − Taxable.
///   5. GrandTotal = nearest whole rupee, half up; difference → RoundingAdjustment.
///   6. Bill of Supply (no GSTIN): no TaxSplits, TaxTotal = 0, totals otherwise identical.
/// All rounding is half-up on non-negative integers. All arithmetic is checked (M7): overflow throws
/// OverflowException, which FinalizeBill maps to BILL_AMOUNT_TOO_LARGE — never a silent wrap.
/// </summary>
public static class BillCalculator
{
    /// <summary>Fills every computed field on the bill and its lines. Does not change Status.</summary>
    /// <exception cref="ArgumentException">Empty bill or a non-positive quantity/price (validate first).</exception>
    public static void Compute(Bill bill, bool shopHasGstin)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (bill.Lines.Count == 0) throw new ArgumentException("Bill has no lines.", nameof(bill));

        // 1–2. Lines.
        foreach (var line in bill.Lines)
        {
            if (line.Qty.Milli <= 0) throw new ArgumentException($"Line {line.Id}: quantity must be positive.");
            if (line.UnitPrice.Value < 0) throw new ArgumentException($"Line {line.Id}: price cannot be negative.");

            line.Gross = new Paise(DivHalfUp(checked(line.UnitPrice.Value * line.Qty.Milli), 1000));
            line.LineTotal = line.Gross - new Paise(DiscountAmount(line.Gross.Value, line.LineDiscount));
            line.BillDiscountShare = Paise.Zero;
        }

        var subTotal = bill.Lines.Sum(l => l.LineTotal.Value);   // Enumerable.Sum(long) is checked
        bill.SubTotal = new Paise(subTotal);

        // 3. Bill discount, apportioned by largest remainder.
        var billDiscount = DiscountAmount(subTotal, bill.BillDiscount);
        bill.BillDiscountAmount = new Paise(billDiscount);
        if (billDiscount > 0 && subTotal > 0) Apportion(bill.Lines, billDiscount, subTotal);

        // 4. Tax per slab.
        bill.IsTaxInvoice = shopHasGstin;
        bill.TaxSplits = shopHasGstin
            ? bill.Lines
                .GroupBy(l => l.GstSlab)
                .OrderBy(g => (int)g.Key)
                .Select(g => SplitFor(g.Key, g.Sum(l => l.Net.Value)))
                .ToList()
            : [];
        bill.TaxTotal = new Paise(bill.TaxSplits.Sum(s => s.Tax.Value));

        // 5. Whole-rupee rounding.
        var net = subTotal - billDiscount;
        var grand = DivHalfUp(net, 100) * 100;
        bill.GrandTotal = new Paise(grand);
        bill.RoundingAdjustment = new Paise(grand - net);
    }

    /// <summary>B1: change exists only for cash. UPI/udhaar bills never show a (negative) change due.</summary>
    public static Paise ChangeDue(Bill bill) =>
        bill.Tender == TenderType.Cash && bill.CashReceived > bill.GrandTotal ? bill.CashReceived - bill.GrandTotal : Paise.Zero;

    /// <summary>Line discounts plus bill discount, for the day-end report.</summary>
    public static Paise TotalDiscount(Bill bill)
        => new(bill.Lines.Sum(l => l.Gross.Value - l.LineTotal.Value) + bill.BillDiscountAmount.Value);

    internal static TaxSplit SplitFor(GstSlab slab, long gross)
    {
        var bp = (long)slab;
        var taxable = bp == 0 ? gross : DivHalfUp(checked(gross * 10000), 10000 + bp);
        return new TaxSplit(slab, new Paise(taxable), new Paise(gross - taxable));
    }

    internal static long DiscountAmount(long baseAmount, Discount discount) => discount.Kind switch
    {
        DiscountKind.PercentBasisPoints => DivHalfUp(checked(baseAmount * Math.Clamp(discount.Amount, 0, 10000)), 10000),
        DiscountKind.FlatPaise => Math.Clamp(discount.Amount, 0, baseAmount),
        _ => 0,
    };

    private static void Apportion(List<BillLine> lines, long discount, long subTotal)
    {
        var raw = new long[lines.Count];
        var rem = new long[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var weighted = checked(lines[i].LineTotal.Value * discount);
            raw[i] = weighted / subTotal;
            rem[i] = weighted % subTotal;
        }

        var left = discount - raw.Sum();
        foreach (var i in Enumerable.Range(0, lines.Count).OrderByDescending(i => rem[i]).ThenBy(i => i).Take((int)left))
            raw[i]++;

        for (var i = 0; i < lines.Count; i++) lines[i].BillDiscountShare = new Paise(raw[i]);
    }

    /// <summary>Round-half-up integer division for non-negative numerator, positive denominator.</summary>
    internal static long DivHalfUp(long numerator, long denominator)
    {
        if (numerator < 0) throw new ArgumentOutOfRangeException(nameof(numerator), "Money math works on non-negative amounts.");
        return checked(2 * numerator + denominator) / checked(2 * denominator);   // M7: absurd inputs fail loud
    }
}
