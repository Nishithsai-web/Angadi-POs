using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Domain.Billing;

/// <summary>
/// v2 returns / partial refunds: a credit-note-style document for goods brought back from a finalized bill.
/// The bill itself never changes (it stays immutable); the return carries its own number (R-000001), links to the
/// bill, and values each returned quantity at the original line's price, discounts and GST (docs/BILLING_RULES.md).
/// </summary>
public sealed class SalesReturn : Entity
{
    public string Number { get; set; } = "";
    public Guid BillId { get; set; }
    public string BillNumber { get; set; } = "";
    public Guid? CustomerId { get; set; }
    public List<ReturnLine> Lines { get; set; } = [];

    /// <summary>Σ line amounts (GST-inclusive, after the original discounts), in paise.</summary>
    public Paise LinesTotal { get; set; }
    /// <summary>Whole-rupee rounding of the refund, explicit like the bill's (cumulative per bill, see ReturnCalculator).</summary>
    public Paise RoundingAdjustment { get; set; }
    /// <summary>What goes back to the customer, in whole rupees: CashRefund + UpiRefund + DuesCredit.</summary>
    public Paise RefundTotal { get; set; }

    /// <summary>Copied from the bill: a Tax Invoice's return reverses GST; a Bill of Supply's carries none.</summary>
    public bool IsTaxInvoice { get; set; }
    public List<TaxSplit> TaxSplits { get; set; } = [];
    public Paise TaxTotal { get; set; }

    public RefundMethod Method { get; set; }
    public Paise CashRefund { get; set; }
    public Paise UpiRefund { get; set; }
    /// <summary>Udhaar cancelled instead of money handed back (never more than the customer owed).</summary>
    public Paise DuesCredit { get; set; }
    public string? UpiRef { get; set; }
    public string? Reason { get; set; }

    public DateTimeOffset ReturnedAt { get; set; }
}

public sealed class ReturnLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The bill line this quantity came from.</summary>
    public Guid BillLineId { get; set; }
    public Guid ItemId { get; set; }
    public string TeluguName { get; set; } = "";
    public string EnglishName { get; set; } = "";
    public Unit Unit { get; set; }
    public GstSlab GstSlab { get; set; }
    public Qty Qty { get; set; }
    /// <summary>GST-inclusive value of this quantity, after the line's and the bill's discounts.</summary>
    public Paise Amount { get; set; }
}

/// <summary>How a return's refund goes back: cash, UPI, or by cutting the customer's udhaar (excess → cash).</summary>
public enum RefundMethod { Cash = 0, Upi = 1, Dues = 2 }

/// <summary>
/// Money maths for returns, pure and exact. Every amount is CUMULATIVE per bill line / per bill, so partial returns
/// can never add up to more than was paid, and returning everything gives back exactly the bill:
///   line amount = round(Net × returnedSoFar / sold) − round(Net × returnedBefore / sold)
///   refund      = rupee(Σ amounts of all returns so far) − rupee(Σ amounts of earlier returns)
///   tax (slab)  = split(Σ slab amounts so far) − split(Σ slab amounts before)
/// where rupee() is the bill's own whole-rupee half-up rounding and split() the bill's own GST extraction.
/// </summary>
public static class ReturnCalculator
{
    /// <summary>The value of returning <paramref name="qty"/> more of <paramref name="line"/> after <paramref name="before"/> already came back.</summary>
    public static Paise LineAmount(BillLine line, Qty before, Qty qty)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Qty.Milli <= 0) throw new ArgumentException("The bill line has no quantity.", nameof(line));
        if (qty.Milli <= 0 || before.Milli < 0 || checked(before.Milli + qty.Milli) > line.Qty.Milli)
            throw new ArgumentOutOfRangeException(nameof(qty), "Return quantity must be positive and within what was sold.");
        var net = line.Net.Value;
        return new Paise(Share(net, before.Milli + qty.Milli, line.Qty.Milli) - Share(net, before.Milli, line.Qty.Milli));
    }

    /// <summary>Fills LinesTotal, RefundTotal, RoundingAdjustment and the tax fields from the lines' amounts.</summary>
    /// <param name="ret">The return; its Lines and IsTaxInvoice must be set.</param>
    /// <param name="earlier">This bill's earlier returns (refund and tax are cumulative, so the parts add up exactly).</param>
    public static void Compute(SalesReturn ret, IEnumerable<SalesReturn> earlier)
    {
        ArgumentNullException.ThrowIfNull(ret);
        ArgumentNullException.ThrowIfNull(earlier);
        var before = earlier.ToList();
        var earlierTotal = before.Sum(r => r.LinesTotal.Value);
        var lines = ret.Lines.Sum(l => l.Amount.Value);
        ret.LinesTotal = new Paise(lines);
        var refund = Rupee(checked(earlierTotal + lines)) - Rupee(earlierTotal);
        ret.RefundTotal = new Paise(refund);
        ret.RoundingAdjustment = new Paise(refund - lines);
        if (!ret.IsTaxInvoice)
        {
            ret.TaxSplits = [];
            ret.TaxTotal = Paise.Zero;
            return;
        }
        // Tax per slab, cumulative like the refund: returning a whole bill in parts reverses exactly the bill's GST.
        var earlierBySlab = before.SelectMany(r => r.Lines).GroupBy(l => l.GstSlab)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Amount.Value));
        ret.TaxSplits = ret.Lines.GroupBy(l => l.GstSlab).OrderBy(g => (int)g.Key).Select(g =>
        {
            var was = earlierBySlab.TryGetValue(g.Key, out var e) ? e : 0;
            var now = checked(was + g.Sum(l => l.Amount.Value));
            var after = BillCalculator.SplitFor(g.Key, now);
            var prior = BillCalculator.SplitFor(g.Key, was);
            return new TaxSplit(g.Key, after.Taxable - prior.Taxable, after.Tax - prior.Tax);
        }).ToList();
        ret.TaxTotal = new Paise(ret.TaxSplits.Sum(s => s.Tax.Value));
    }

    private static long Share(long net, long part, long whole) => BillCalculator.DivHalfUp(checked(net * part), whole);
    private static long Rupee(long paise) => checked(BillCalculator.DivHalfUp(paise, 100) * 100);
}
