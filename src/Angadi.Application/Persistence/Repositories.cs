using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Events;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;

namespace Angadi.Application.Persistence;

/// <summary>
/// One transaction per command (blueprint §4). TransactionBehavior wraps each command handler;
/// handlers never commit. A bill never half-saves (FR-15).
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction. Commits only when
    /// <paramref name="shouldCommit"/> returns true for the result (e.g. Result.IsSuccess);
    /// rolls back on a failed result or any exception. Repository calls inside join the transaction.
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<Task<T>> work, Func<T, bool> shouldCommit, CancellationToken ct = default);
}

public interface IItemRepository
{
    Task<Item?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Item?> GetByBarcodeAsync(string barcode, CancellationToken ct = default);   // indexed, FR-08
    /// <summary>Matches Telugu name, English name, aliases or barcode prefix (FR-05).</summary>
    Task<IReadOnlyList<Item>> SearchAsync(string text, int limit = 20, CancellationToken ct = default);
    /// <summary>All active items; the voice matcher scores against this list.</summary>
    Task<IReadOnlyList<Item>> GetActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Item>> GetQuickSaleAsync(CancellationToken ct = default);     // FR-10
    Task<IReadOnlyList<Item>> GetLowStockAsync(CancellationToken ct = default);      // FR-25
    Task SaveAsync(Item item, CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);                              // seeding idempotency
}

public interface IBillRepository
{
    Task<Bill?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Bill>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    /// <summary>Finalized and voided bills whose local finalize day is in [from, to] (reports, FR-27–29).</summary>
    Task<IReadOnlyList<Bill>> ListByDayAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<int> CountFinalizedInMonthAsync(int year, int month, CancellationToken ct = default);   // FR-34
    Task SaveAsync(Bill bill, CancellationToken ct = default);   // bill + lines together
}

public interface ICustomerRepository
{
    Task<Customer?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> SearchAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> GetWithDuesAsync(CancellationToken ct = default);   // FR-30
    Task<IReadOnlyList<DuesEntry>> GetLedgerAsync(Guid customerId, CancellationToken ct = default);
    Task SaveAsync(Customer customer, CancellationToken ct = default);
    Task AddDuesEntryAsync(DuesEntry entry, CancellationToken ct = default);
}

/// <summary>
/// The only way stock changes. Writes the StockMove and updates Item.Stock in the current
/// transaction (FR-24, NFR-06). Must be safe under concurrent adjusts (no lost updates).
/// </summary>
public interface IStockLedger
{
    Task AdjustAsync(Guid itemId, Qty delta, StockMoveReason reason, Guid? billId = null, string? note = null, CancellationToken ct = default);
    Task<IReadOnlyList<StockMove>> GetHistoryAsync(Guid itemId, CancellationToken ct = default);   // FR-26
    /// <summary>Σ sold qty per item per day, for insights (FR-31/32).</summary>
    Task<IReadOnlyList<DailySales>> GetDailySalesAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

public sealed record DailySales(Guid ItemId, DateOnly Day, Qty Sold);

/// <summary>v2 sync seam (ADR-3). v1 appends only; nothing drains it yet.</summary>
public interface IOutbox
{
    Task AppendAsync(string aggregateType, IDomainEvent @event, CancellationToken ct = default);
}

public interface IShopProfileRepository
{
    Task<ShopProfile?> GetAsync(CancellationToken ct = default);
    Task SaveAsync(ShopProfile profile, CancellationToken ct = default);
    /// <summary>Atomically returns the next number ("A-000123") and increments the sequence.</summary>
    Task<string> NextBillNumberAsync(CancellationToken ct = default);
}

/// <summary>
/// H2/B2: the free-tier counter. Per-month buckets in Meta keyed by the "effective month" =
/// local month of max(now, last time the app saw the clock), so rolling the clock back never
/// reopens a spent month and rolling it forward only spends a future bucket. Called inside the
/// finalize transaction, so check + increment are atomic. Reinstall/factory reset still wipes it
/// (documented; v2 moves the entitlement to a server).
/// </summary>
public interface IQuotaStore
{
    Task<int> UsedThisMonthAsync(DateTimeOffset now, CancellationToken ct = default);
    Task IncrementAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>FR-04. Idempotent: does nothing if items exist.</summary>
public interface ISeedService
{
    Task<int> SeedAsync(bool kirana, bool pharmacy, CancellationToken ct = default);
}
