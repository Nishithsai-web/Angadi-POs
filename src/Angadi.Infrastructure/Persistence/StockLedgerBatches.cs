using Angadi.Application.Persistence;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using static Angadi.Infrastructure.Persistence.Mapping;

namespace Angadi.Infrastructure.Persistence;

/// <summary>v2-stock: the batch side of the one stock ledger (same class, same connection, same transaction).</summary>
internal sealed partial class StockLedger : IBatchLedger
{
    public Task<IReadOnlyList<Batch>> GetBatchesAsync(Guid itemId, CancellationToken ct = default)
        => Db.RunAsync<IReadOnlyList<Batch>>(c => BatchSql.Load(c, itemId).Select(BatchSql.ToDomain).ToList(), ct);

    public Task<IReadOnlyList<Batch>> GetStockedBatchesAsync(CancellationToken ct = default)
        => Db.RunAsync<IReadOnlyList<Batch>>(c => c.Query<BatchRow>("SELECT * FROM Batch WHERE QtyMilli <> 0")
            .Select(BatchSql.ToDomain).ToList(), ct);

    public Task<Guid?> ReceiveAsync(Guid itemId, Qty qty, StockMoveReason reason, Guid? purchaseId, string? batchNo, DateOnly? expiry,
        Paise cost, string? note, CancellationToken ct = default)
    {
        var now = Clock.Now;
        var ms = Ms(now);
        return Db.RunAsync(c =>
        {
            Guid? result = null;
            c.RunInTransactionIfNeeded(() =>
            {
                if (!BatchSql.Tracks(c, itemId))
                {
                    // Not batch-tracked: one plain move, as v1 stock-in, linked to the purchase.
                    if (c.Execute("UPDATE Item SET StockMilli = StockMilli + ?, UpdatedAtMs = ? WHERE Id = ?", qty.Milli, ms, itemId) != 1)
                        throw new InvalidOperationException($"Stock adjust for unknown item {itemId}");
                    c.Insert(new StockMoveRow
                    {
                        Id = Guid.NewGuid(), ItemId = itemId, DeltaMilli = qty.Milli, Reason = (int)reason, Note = note,
                        Day = Day(now), CreatedAtMs = ms, PurchaseId = purchaseId,
                    });
                    return;
                }

                var no = (batchNo ?? "").Trim().ToUpperInvariant();
                int? expiryDay = expiry is { } e ? Day(e) : null;
                BatchRow? batch = no.Length == 0 && expiryDay is null
                    ? BatchSql.Placeholder(c, itemId, ms)
                    : c.Query<BatchRow>("SELECT * FROM Batch WHERE ItemId = ? AND BatchNo = ? AND ExpiryDay IS ?", itemId, no, expiryDay).FirstOrDefault();
                if (batch is null)
                {
                    batch = new BatchRow { Id = Guid.NewGuid(), ItemId = itemId, BatchNo = no, ExpiryDay = expiryDay, CostPaise = cost.Value, CreatedAtMs = ms, UpdatedAtMs = ms };
                    c.Insert(batch);
                }
                else if (cost.Value > 0)
                {
                    c.Execute("UPDATE Batch SET CostPaise = ? WHERE Id = ?", cost.Value, batch.Id);
                }
                BatchSql.Move(c, itemId, batch.Id, qty.Milli, reason, null, purchaseId, note, now);
                result = batch.Id;
            });
            return result;
        }, ct);
    }

    public Task AdjustBatchAsync(Guid itemId, Guid batchId, Qty delta, StockMoveReason reason, string? note, CancellationToken ct = default)
    {
        var now = Clock.Now;
        return Db.RunAsync(c => c.RunInTransactionIfNeeded(() => BatchSql.Move(c, itemId, batchId, delta.Milli, reason, null, null, note, now)), ct);
    }

    public Task<IReadOnlyDictionary<Guid, long>> BatchTotalsAsync(CancellationToken ct = default)
        => Db.RunAsync<IReadOnlyDictionary<Guid, long>>(c => c.Query<BatchTotalRow>(
                "SELECT b.ItemId AS ItemId, SUM(b.QtyMilli) AS TotalMilli FROM Batch b JOIN Item i ON i.Id = b.ItemId " +
                "WHERE COALESCE(i.TracksBatches, 0) = 1 GROUP BY b.ItemId")
            .ToDictionary(r => r.ItemId, r => r.TotalMilli), ct);

    private sealed class BatchTotalRow
    {
        public Guid ItemId { get; set; }
        public long TotalMilli { get; set; }
    }
}
