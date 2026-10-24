using Angadi.Application.Common;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Purchasing;

namespace Angadi.Application.Stock;

// v2-stock use cases: suppliers, purchase entry, purchase orders, batch & expiry, purchase reports.

// ---- Suppliers
public sealed record SaveSupplierCommand(Supplier Supplier) : ICommand<Result<Guid>>;
public sealed record ListSuppliersQuery : IQuery<IReadOnlyList<Supplier>>;

// ---- Purchase entry (a supplier bill into stock)
/// <summary>
/// Adds every line's quantity through the stock ledger (reason Purchase, linked to the purchase) in ONE transaction,
/// creates/extends batches for batch-tracked items, and saves each item's latest cost. Saved purchases are never
/// edited: mistakes are corrected with a stock adjustment.
/// </summary>
public sealed record RecordPurchaseCommand(PurchaseEntry Entry) : ICommand<Result<Guid>>;

public sealed record PurchaseEntry(
    Guid SupplierId, string SupplierBillNo, DateOnly BillDate, IReadOnlyList<PurchaseEntryLine> Lines, Guid? PurchaseOrderId = null);

/// <param name="Qty">In the item's own unit.</param>
/// <param name="CostPrice">Per whole unit, before GST, as on the supplier's bill.</param>
public sealed record PurchaseEntryLine(Guid ItemId, Qty Qty, Paise CostPrice, GstSlab GstSlab, string? BatchNo = null, DateOnly? Expiry = null);

public sealed record ListPurchasesQuery(DateOnly From, DateOnly To) : IQuery<IReadOnlyList<Purchase>>;
public sealed record GetPurchaseQuery(Guid PurchaseId) : IQuery<Purchase?>;

// ---- Purchase orders
/// <summary>Creates or edits a DRAFT order (sent / received orders are frozen).</summary>
public sealed record SavePurchaseOrderCommand(PurchaseOrder Order) : ICommand<Result<Guid>>;
public sealed record ListPurchaseOrdersQuery : IQuery<IReadOnlyList<PurchaseOrder>>;
public sealed record GetPurchaseOrderQuery(Guid OrderId) : IQuery<PurchaseOrder?>;
/// <summary>Lines for items the insights forecast says run out within <paramref name="WithinDays"/> days, plus items already low.</summary>
public sealed record SuggestOrderFromLowStockQuery(int WithinDays = 7) : IQuery<IReadOnlyList<PurchaseOrderLine>>;
/// <summary>Draft → Sent. Received happens only through a purchase entry made from the order.</summary>
public sealed record MarkPurchaseOrderSentCommand(Guid OrderId) : ICommand<Result>;
/// <summary>Opens the share sheet with the order as plain text, then marks a draft Sent.</summary>
public sealed record SharePurchaseOrderCommand(Guid OrderId) : ICommand<Result>, INoTransaction;
/// <summary>"Receive": a purchase entry pre-filled from the order (latest cost and the item's GST slab).</summary>
public sealed record PrefillPurchaseFromOrderQuery(Guid OrderId) : IQuery<Result<PurchaseEntry>>;

// ---- Batch & expiry
public sealed record GetItemBatchesQuery(Guid ItemId) : IQuery<IReadOnlyList<Batch>>;
/// <summary>Stock-in / damage / count correction on one batch of a batch-tracked item.</summary>
public sealed record AdjustBatchStockCommand(Guid ItemId, Guid BatchId, Qty Delta, StockMoveReason Reason, string? Note = null) : ICommand<Result>;
/// <summary>Stock-in of a new (or existing) batch from the item screen, without a supplier bill.</summary>
public sealed record StockInBatchCommand(Guid ItemId, string BatchNo, DateOnly Expiry, Qty Qty, string? Note = null) : ICommand<Result>;

/// <summary>Batches that are expired or expire within <paramref name="WithinDays"/> days, with value at cost.</summary>
public sealed record GetExpiringQuery(int WithinDays = 90) : IQuery<ExpiringReport>;
public sealed record ExpiringReport(ExpiryBucket Expired, ExpiryBucket Within30, ExpiryBucket Within60, ExpiryBucket Within90, IReadOnlyList<ExpiringLine> Lines)
{
    public bool HasAlerts => Expired.Count > 0 || Within30.Count > 0;
}
public sealed record ExpiryBucket(int Count, Paise Value);
public sealed record ExpiringLine(Guid ItemId, Guid BatchId, string Name, Unit Unit, string BatchNo, DateOnly Expiry, Qty Qty, Paise Value, int DaysLeft);

/// <summary>What a bill would sell from expired batches (empty = nothing expired). Used before finalize.</summary>
public sealed record CheckExpiredSaleQuery(IReadOnlyList<SaleQty> Lines) : IQuery<IReadOnlyList<ExpiredSaleLine>>;
public sealed record SaleQty(Guid ItemId, Qty Qty);
public sealed record ExpiredSaleLine(Guid ItemId, string Name, string BatchNo, DateOnly? Expiry, Qty Qty);

// ---- Reports
public sealed record GetPurchasesBySupplierQuery(DateOnly From, DateOnly To) : IQuery<IReadOnlyList<SupplierPurchaseTotal>>;
public sealed record SupplierPurchaseTotal(Guid SupplierId, string SupplierName, int Bills, Paise Total);

/// <summary>
/// ESTIMATE only: sales before GST minus quantity × the item's latest cost, over sold lines whose item has a known
/// cost. Lines without a cost are counted, not guessed.
/// </summary>
public sealed record GetMarginEstimateQuery(DateOnly From, DateOnly To) : IQuery<MarginEstimate>;
public sealed record MarginEstimate(Paise SalesBeforeGst, Paise Cost, Paise Margin, int LinesWithCost, int LinesWithoutCost)
{
    /// <summary>Margin as basis points of sales (2500 = 25%); 0 when nothing with a known cost was sold.</summary>
    public long MarginBasisPoints => SalesBeforeGst.Value <= 0 ? 0 : Margin.Value * 10_000 / SalesBeforeGst.Value;
}
