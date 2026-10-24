using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Domain.Purchasing;

/// <summary>v2-stock: who the shop buys from. The phone number is never logged.</summary>
public sealed class Supplier : Entity
{
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Gstin { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// v2-stock: one supplier bill entered into stock. Immutable once saved: a mistake is corrected with a stock
/// adjustment (damage / count correction on the batch), never by editing the purchase — the ledger stays the history.
/// </summary>
public sealed class Purchase : Entity
{
    public Guid SupplierId { get; set; }
    /// <summary>Snapshot of the supplier's name when the bill was entered.</summary>
    public string SupplierName { get; set; } = "";
    public string SupplierBillNo { get; set; } = "";
    public DateOnly BillDate { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public List<PurchaseLine> Lines { get; set; } = [];
    /// <summary>Σ line totals, GST included.</summary>
    public Paise Total { get; set; }
}

public sealed class PurchaseLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; }
    public string TeluguName { get; set; } = "";
    public string EnglishName { get; set; } = "";
    public Unit Unit { get; set; }
    /// <summary>In the item's own unit (milli-units).</summary>
    public Qty Qty { get; set; }
    /// <summary>Cost per whole unit, before GST, as printed on the supplier's bill.</summary>
    public Paise CostPrice { get; set; }
    public GstSlab GstSlab { get; set; }
    public string? BatchNo { get; set; }
    public DateOnly? Expiry { get; set; }
    public Guid? BatchId { get; set; }
    public Paise LineTotal { get; set; }

    /// <summary>round(qty × cost × (1 + GST)), half up, in checked whole-paise maths.</summary>
    public static Paise Total(Qty qty, Paise cost, GstSlab slab)
    {
        var numerator = checked(qty.Milli * cost.Value * (10_000 + (long)slab));
        const long Denominator = 10_000_000;   // 1000 milli × 10000 basis points
        return new Paise((numerator + Denominator / 2) / Denominator);
    }
}

public enum PurchaseOrderStatus { Draft = 0, Sent = 1, Received = 2 }

/// <summary>v2-stock: what the shop means to order. Draft → Sent (shared as text) → Received (by a purchase entry).</summary>
public sealed class PurchaseOrder : Entity
{
    public Guid SupplierId { get; set; }
    public PurchaseOrderStatus Status { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
    public Guid? PurchaseId { get; set; }
}

public sealed record PurchaseOrderLine(Guid ItemId, Qty Qty);
