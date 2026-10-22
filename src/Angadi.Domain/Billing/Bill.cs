using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Domain.Billing;

/// <summary>
/// Ticket model (SambaPOS pattern): Draft while being built, Finalized on tender, Void on reversal.
/// A finalized bill is immutable; corrections are a Void (FR-16), never an edit.
/// </summary>
public sealed class Bill : Entity
{
    /// <summary>Human-readable, per-device sequence, e.g. "A-000123". Printed on receipt.</summary>
    public string Number { get; set; } = "";
    public BillStatus Status { get; set; } = BillStatus.Draft;
    public List<BillLine> Lines { get; set; } = [];

    public Discount BillDiscount { get; set; } = Discount.None;   // FR-12
    public TenderType? Tender { get; set; }                         // FR-14, single tender in v1
    public Guid? CustomerId { get; set; }                           // required when Tender == Udhaar
    public Paise CashReceived { get; set; }                         // for change due
    /// <summary>H9: UPI transaction reference (UTR / last digits) for reconciling UPI bills. Optional.</summary>
    public string? UpiRef { get; set; }

    // Computed by BillCalculator at finalize, then frozen:
    public Paise SubTotal { get; set; }        // sum of line totals after line discounts
    public Paise BillDiscountAmount { get; set; }
    public Paise TaxTotal { get; set; }
    public Paise RoundingAdjustment { get; set; }   // FR-13, explicit line on receipt
    public Paise GrandTotal { get; set; }           // whole rupees
    public List<TaxSplit> TaxSplits { get; set; } = [];

    /// <summary>True when the shop had a GSTIN at finalize → "Tax Invoice"; else "Bill of Supply" (FR-18).</summary>
    public bool IsTaxInvoice { get; set; }

    public DateTimeOffset? FinalizedAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
}

public sealed class BillLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; }
    /// <summary>Snapshots, so later item edits never change an old bill.</summary>
    public string TeluguName { get; set; } = "";
    public string EnglishName { get; set; } = "";
    public Unit Unit { get; set; }
    public Paise UnitPrice { get; set; }     // GST-inclusive, editable before finalize (FR-11)
    public GstSlab GstSlab { get; set; }
    public Qty Qty { get; set; }
    public Discount LineDiscount { get; set; } = Discount.None;
    public Paise Gross { get; set; }         // round(UnitPrice × Qty), before any discount
    public Paise LineTotal { get; set; }     // after line discount, GST-inclusive
    public Paise BillDiscountShare { get; set; }   // this line's part of the bill discount
    public Paise Net => LineTotal - BillDiscountShare;
    public LineSource Source { get; set; }   // analytics + voice accuracy tracking
}

/// <summary>
/// One GST slab of a bill. A2-02: an intra-state Tax Invoice shows CGST and SGST separately (CGST Rule 46(i)/(j)),
/// each at half the slab rate. CGST = Tax / 2 rounded half up; SGST = Tax − CGST, so the two always add up to
/// exactly <see cref="Tax"/> and no total changes. The GST summary applies the same rule to each slab's total tax.
/// Intra-state only: v1 has no IGST.
/// </summary>
public sealed record TaxSplit(GstSlab Slab, Paise Taxable, Paise Tax)
{
    public Paise Cgst => new((Tax.Value + 1) / 2);
    public Paise Sgst => Tax - Cgst;
    /// <summary>The CGST (= SGST) rate in basis points: half the slab.</summary>
    public int HalfRateBasisPoints => (int)Slab / 2;
}

public enum BillStatus { Draft = 0, Finalized = 1, Void = 2 }
public enum TenderType { Cash = 0, Upi = 1, Udhaar = 2 }
public enum LineSource { Manual = 0, Voice = 1, Barcode = 2, QuickSale = 3 }
