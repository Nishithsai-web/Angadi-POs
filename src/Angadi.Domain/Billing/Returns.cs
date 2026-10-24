using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Domain.Billing;

/// <summary>
/// v2 returns / partial refunds: a credit-note style document against a finalized bill. The bill itself never
/// changes; the return has its own number ("R-000001"), date, lines and refund. Rules: docs/BILLING_RULES.md §Returns.
/// </summary>
public sealed class BillReturn : Entity
{
    public string Number { get; set; } = "";
    public Guid BillId { get; set; }
    public string BillNumber { get; set; } = "";
    public Guid? CustomerId { get; set; }
    public DateTimeOffset ReturnedAt { get; set; }
    public List<ReturnLine> Lines { get; set; } = [];
    /// <summary>Σ line amounts (GST-inclusive, paise).</summary>
    public Paise LinesTotal { get; set; }
    /// <summary>Refund − LinesTotal: the bill's whole-rupee rounding, carried over cumulatively.</summary>
    public Paise RoundingAdjustment { get; set; }
    /// <summary>What the customer gets back in total, whole rupees.</summary>
    public Paise Refund { get; set; }
    public RefundMethod Method { get; set; }
    /// <summary>Part of the refund taken off the customer's udhaar (Method = Dues only).</summary>
    public Paise DuesReduced { get; set; }
    /// <summary>Part of the refund handed over in cash.</summary>
    public Paise RefundCash { get; set; }
    /// <summary>Part of the refund sent by UPI.</summary>
    public Paise RefundUpi { get; set; }
    public string? UpiRef { get; set; }
    public string? Reason { get; set; }
    public bool IsTaxInvoice { get; set; }
    public Paise TaxTotal => new(Lines.Sum(l => l.Tax.Value));
}

public sealed class ReturnLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BillLineId { get; set; }
    public Guid ItemId { get; set; }
    public string TeluguName { get; set; } = "";
    public string EnglishName { get; set; } = "";
    public Unit Unit { get; set; }
    public GstSlab GstSlab { get; set; }
    public Qty Qty { get; set; }
    /// <summary>GST-inclusive amount refunded for this line (its share of the original line's net).</summary>
    public Paise Amount { get; set; }
    public Paise Taxable { get; set; }
    public Paise Tax { get; set; }
}

/// <summary>Cash back, UPI back, or off the customer's udhaar (excess over the dues is paid in cash).</summary>
public enum RefundMethod { Cash = 0, Upi = 1, Dues = 2 }

public sealed record ReturnRequestLine(Guid BillLineId, Qty Qty);

public enum ReturnProblem { None = 0, NothingSelected = 1, UnknownLine = 2, TooMuch = 3 }

/// <summary>
/// Pure, deterministic return maths. Everything is cumulative over the bill's returns, so returning a line (or the
/// whole bill) in any number of steps refunds exactly what was paid — never a paisa more or less:
///   line amount = round(Net × returnedAfter / sold) − round(Net × returnedBefore / sold)   (same for tax);
///   refund      = rupee(Σ returned net after) − rupee(Σ returned net before), rupee() = the bill's half-up rounding.
/// </summary>
public static class ReturnCalculator
{
    /// <summary>Quantity of each bill line already returned by <paramref name="previous"/>.</summary>
    public static Dictionary<Guid, long> ReturnedSoFar(IEnumerable<BillReturn> previous)
    {
        var returned = new Dictionary<Guid, long>();
        foreach (var l in previous.SelectMany(r => r.Lines))
            returned[l.BillLineId] = checked((returned.TryGetValue(l.BillLineId, out var q) ? q : 0) + l.Qty.Milli);
        return returned;
    }

    /// <summary>What can still come back for a line: sold minus already returned.</summary>
    public static Qty Returnable(BillLine line, IReadOnlyDictionary<Guid, long> returnedSoFar) =>
        new(line.Qty.Milli - (returnedSoFar.TryGetValue(line.Id, out var q) ? q : 0));

    /// <summary>
    /// Builds the lines and refund for a return. Requests for the same line are merged. Does not set number, date,
    /// method or refund split.
    /// </summary>
    public static (BillReturn? Return, ReturnProblem Problem) Compute(Bill bill, IEnumerable<BillReturn> previous, IEnumerable<ReturnRequestLine> request)
    {
        ArgumentNullException.ThrowIfNull(bill);
        var prior = previous.ToList();
        var returned = ReturnedSoFar(prior);
        var wanted = request.Where(r => r.Qty.Milli != 0)
            .GroupBy(r => r.BillLineId)
            .Select(g => (LineId: g.Key, Qty: g.Sum(r => r.Qty.Milli)))
            .ToList();
        if (wanted.Count == 0) return (null, ReturnProblem.NothingSelected);

        var result = new BillReturn { BillId = bill.Id, BillNumber = bill.Number, CustomerId = bill.CustomerId, IsTaxInvoice = bill.IsTaxInvoice };
        foreach (var line in bill.Lines)   // bill order, so the document reads like the bill
        {
            var want = wanted.FirstOrDefault(w => w.LineId == line.Id);
            if (want.LineId == Guid.Empty) continue;
            var before = returned.TryGetValue(line.Id, out var q) ? q : 0;
            if (want.Qty <= 0 || checked(before + want.Qty) > line.Qty.Milli) return (null, ReturnProblem.TooMuch);
            var after = before + want.Qty;

            var net = line.Net.Value;
            var tax = bill.IsTaxInvoice ? BillCalculator.SplitFor(line.GstSlab, net).Tax.Value : 0;
            var amount = Share(net, after, line.Qty.Milli) - Share(net, before, line.Qty.Milli);
            var taxPart = Share(tax, after, line.Qty.Milli) - Share(tax, before, line.Qty.Milli);
            result.Lines.Add(new ReturnLine
            {
                BillLineId = line.Id, ItemId = line.ItemId, TeluguName = line.TeluguName, EnglishName = line.EnglishName,
                Unit = line.Unit, GstSlab = line.GstSlab, Qty = new Qty(want.Qty),
                Amount = new Paise(amount), Tax = new Paise(taxPart), Taxable = new Paise(amount - taxPart),
            });
        }
        if (result.Lines.Count != wanted.Count) return (null, ReturnProblem.UnknownLine);

        // Cumulative whole-rupee refund over the whole bill.
        long NetReturned(Func<BillLine, long> qtyOf) => bill.Lines.Sum(l => Share(l.Net.Value, qtyOf(l), l.Qty.Milli));
        var netBefore = NetReturned(l => returned.TryGetValue(l.Id, out var b) ? b : 0);
        var netAfter = NetReturned(l =>
            (returned.TryGetValue(l.Id, out var b) ? b : 0) + result.Lines.Where(r => r.BillLineId == l.Id).Sum(r => r.Qty.Milli));
        result.LinesTotal = new Paise(result.Lines.Sum(l => l.Amount.Value));
        result.Refund = new Paise(Rupee(netAfter) - Rupee(netBefore));
        result.RoundingAdjustment = result.Refund - result.LinesTotal;
        return (result, ReturnProblem.None);
    }

    /// <summary>round_half_up(value × part / whole) in 128-bit, so no realistic bill can overflow.</summary>
    internal static long Share(long value, long part, long whole)
    {
        if (whole <= 0 || part <= 0) return 0;
        if (part >= whole) return value;
        var num = (Int128)value * part;
        return (long)((2 * num + whole) / (2 * (Int128)whole));
    }

    private static long Rupee(long paise) => (2 * paise + 100) / 200 * 100;
}
