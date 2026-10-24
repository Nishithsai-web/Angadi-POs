using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Purchasing;

namespace Angadi.Application.Persistence;

/// <summary>v2-stock: suppliers, purchase entries (insert-only) and purchase orders.</summary>
public interface IPurchasingRepository
{
    Task<Supplier?> GetSupplierAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Supplier>> ListSuppliersAsync(CancellationToken ct = default);
    Task SaveSupplierAsync(Supplier supplier, CancellationToken ct = default);

    /// <summary>Inserts a purchase and its lines. Never updates: saved purchases are not editable.</summary>
    Task InsertPurchaseAsync(Purchase purchase, CancellationToken ct = default);
    Task<Purchase?> GetPurchaseAsync(Guid id, CancellationToken ct = default);
    /// <summary>Purchases whose supplier bill date is in [from, to], newest first, with their lines.</summary>
    Task<IReadOnlyList<Purchase>> ListPurchasesAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    /// <summary>True when this supplier's bill number was already entered (a double entry would double the stock).</summary>
    Task<bool> PurchaseBillExistsAsync(Guid supplierId, string supplierBillNo, CancellationToken ct = default);

    Task<PurchaseOrder?> GetOrderAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<PurchaseOrder>> ListOrdersAsync(CancellationToken ct = default);
    Task SaveOrderAsync(PurchaseOrder order, CancellationToken ct = default);

    /// <summary>The item's latest purchase cost (per whole unit, before GST).</summary>
    Task SetItemCostAsync(Guid itemId, Paise cost, CancellationToken ct = default);
}

/// <summary>
/// v2-stock: the batch side of the stock ledger. Every write also writes the StockMove and moves Item.Stock in the
/// current transaction, so Σ batch qty == Item.Stock == Σ moves holds for batch-tracked items.
/// IStockLedger.AdjustAsync itself splits sales FEFO across batches and puts voids/returns back where they came from.
/// </summary>
public interface IBatchLedger
{
    Task<IReadOnlyList<Batch>> GetBatchesAsync(Guid itemId, CancellationToken ct = default);
    /// <summary>Every batch with a non-zero quantity, all items (expiry alerts).</summary>
    Task<IReadOnlyList<Batch>> GetStockedBatchesAsync(CancellationToken ct = default);

    /// <summary>
    /// Stock in. A batch-tracked item finds or creates the batch (same batch no + expiry) and returns its id;
    /// any other item gets a plain move (returns null). Reason Purchase links the move to <paramref name="purchaseId"/>.
    /// </summary>
    Task<Guid?> ReceiveAsync(Guid itemId, Qty qty, StockMoveReason reason, Guid? purchaseId, string? batchNo, DateOnly? expiry,
        Paise cost, string? note, CancellationToken ct = default);

    /// <summary>Damage / count correction / stock-in on one named batch.</summary>
    Task AdjustBatchAsync(Guid itemId, Guid batchId, Qty delta, StockMoveReason reason, string? note, CancellationToken ct = default);

    /// <summary>Σ batch qty per batch-tracked item (the invariant check).</summary>
    Task<IReadOnlyDictionary<Guid, long>> BatchTotalsAsync(CancellationToken ct = default);
}
