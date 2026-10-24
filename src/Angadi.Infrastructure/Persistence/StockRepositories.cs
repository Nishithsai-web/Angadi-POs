using System.Text.Json;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Purchasing;
using SQLite;
using static Angadi.Infrastructure.Persistence.Mapping;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// v2-stock: the batch half of the stock ledger. Every helper runs on the caller's connection inside the caller's
/// transaction, and every quantity change touches Item.StockMilli, Batch.QtyMilli and a StockMove row together —
/// relative UPDATEs only, never read-modify-write (NFR-06).
/// </summary>
internal static class BatchSql
{
    public static bool Tracks(SQLiteConnection c, Guid itemId) =>
        c.ExecuteScalar<int>("SELECT COALESCE(TracksBatches, 0) FROM Item WHERE Id = ?", itemId) == 1;

    public static List<BatchRow> Load(SQLiteConnection c, Guid itemId) =>
        c.Query<BatchRow>("SELECT * FROM Batch WHERE ItemId = ?", itemId);

    public static Batch ToDomain(BatchRow r) => new()
    {
        Id = r.Id, ItemId = r.ItemId, BatchNo = r.BatchNo, Expiry = r.ExpiryDay is { } d ? FromDay(d) : null,
        Qty = new Qty(r.QtyMilli), Cost = new Paise(r.CostPaise), CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.UpdatedAtMs),
    };

    /// <summary>The item's "no batch" row (BatchNo "" and no expiry), created on first need.</summary>
    public static BatchRow Placeholder(SQLiteConnection c, Guid itemId, long nowMs)
    {
        var found = c.Query<BatchRow>("SELECT * FROM Batch WHERE ItemId = ? AND BatchNo = '' AND ExpiryDay IS NULL LIMIT 1", itemId).FirstOrDefault();
        if (found is not null) return found;
        var row = new BatchRow { Id = Guid.NewGuid(), ItemId = itemId, BatchNo = "", ExpiryDay = null, CreatedAtMs = nowMs, UpdatedAtMs = nowMs };
        c.Insert(row);
        return row;
    }

    /// <summary>
    /// Makes Σ batch qty equal Item.Stock by putting the difference into the "no batch" row — used when tracking is
    /// switched on for an item that already has stock (that stock was never recorded against a batch). No stock move:
    /// Item.Stock does not change.
    /// </summary>
    public static void Reconcile(SQLiteConnection c, Guid itemId, long nowMs)
    {
        var stock = c.ExecuteScalar<long>("SELECT StockMilli FROM Item WHERE Id = ?", itemId);
        var batches = c.ExecuteScalar<long>("SELECT COALESCE(SUM(QtyMilli), 0) FROM Batch WHERE ItemId = ?", itemId);
        var diff = stock - batches;
        if (diff == 0) return;
        var placeholder = Placeholder(c, itemId, nowMs);
        c.Execute("UPDATE Batch SET QtyMilli = QtyMilli + ?, UpdatedAtMs = ? WHERE Id = ?", diff, nowMs, placeholder.Id);
    }

    /// <summary>One move on one batch: item stock, batch qty and the ledger row, together.</summary>
    public static void Move(SQLiteConnection c, Guid itemId, Guid batchId, long deltaMilli, StockMoveReason reason,
        Guid? billId, Guid? purchaseId, string? note, DateTimeOffset now)
    {
        var ms = Ms(now);
        if (c.Execute("UPDATE Item SET StockMilli = StockMilli + ?, UpdatedAtMs = ? WHERE Id = ?", deltaMilli, ms, itemId) != 1)
            throw new InvalidOperationException($"Stock adjust for unknown item {itemId}");
        if (c.Execute("UPDATE Batch SET QtyMilli = QtyMilli + ?, UpdatedAtMs = ? WHERE Id = ? AND ItemId = ?", deltaMilli, ms, batchId, itemId) != 1)
            throw new InvalidOperationException($"Batch {batchId} does not belong to item {itemId}");
        c.Insert(new StockMoveRow
        {
            Id = Guid.NewGuid(), ItemId = itemId, DeltaMilli = deltaMilli, Reason = (int)reason, BillId = billId, Note = note,
            Day = Day(now), CreatedAtMs = ms, BatchId = batchId, PurchaseId = purchaseId,
        });
    }

    /// <summary>
    /// A stock change on a batch-tracked item with no batch named: out-going quantity is split FEFO (unexpired
    /// first, then expired, then — stock may go negative as in v1 — the "no batch" row); a positive change that
    /// carries a bill (void / return) goes back to the batches that bill took it from; any other positive change
    /// goes to the "no batch" row.
    /// </summary>
    public static void Apply(SQLiteConnection c, Guid itemId, long deltaMilli, StockMoveReason reason, Guid? billId,
        string? note, DateTimeOffset now, DateOnly today)
    {
        var ms = Ms(now);
        var takes = new List<(Guid BatchId, long Milli)>();
        if (deltaMilli < 0)
        {
            var batches = Load(c, itemId).Select(ToDomain).ToList();
            var split = BatchAllocator.AllocateOut(batches, -deltaMilli, today);
            takes.AddRange(split.Takes.Select(t => (t.BatchId, -t.Milli)));
            if (split.ShortfallMilli > 0) takes.Add((Placeholder(c, itemId, ms).Id, -split.ShortfallMilli));
        }
        else if (billId is { } bill)
        {
            // Net quantity each batch gave this bill so far (sales negative, earlier returns positive).
            var sold = c.Query<BatchNetRow>(
                "SELECT m.BatchId AS BatchId, -SUM(m.DeltaMilli) AS NetMilli, COALESCE(b.ExpiryDay, 99991231) AS ExpiryDay " +
                "FROM StockMove m JOIN Batch b ON b.Id = m.BatchId WHERE m.BillId = ? AND m.ItemId = ? GROUP BY m.BatchId",
                bill, itemId);
            // Latest expiry first: a partial return refills the freshest batch it came from.
            var back = BatchAllocator.AllocateBack(sold.OrderByDescending(s => s.ExpiryDay).Select(s => (s.BatchId, s.NetMilli)), deltaMilli);
            takes.AddRange(back.Takes.Select(t => (t.BatchId, t.Milli)));
            if (back.ShortfallMilli > 0) takes.Add((Placeholder(c, itemId, ms).Id, back.ShortfallMilli));
        }
        else
        {
            takes.Add((Placeholder(c, itemId, ms).Id, deltaMilli));
        }

        foreach (var (batchId, milli) in takes)
            Move(c, itemId, batchId, milli, reason, billId, null, note, now);
    }

    private sealed class BatchNetRow
    {
        public Guid BatchId { get; set; }
        public long NetMilli { get; set; }
        public int ExpiryDay { get; set; }
    }
}

internal sealed class PurchasingRepository(AngadiDb db, IClock clock) : IPurchasingRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<Supplier?> GetSupplierAsync(Guid id, CancellationToken ct = default)
        => db.RunAsync(c => c.Find<SupplierRow>(id) is { } r ? ToDomain(r) : null, ct);

    public Task<IReadOnlyList<Supplier>> ListSuppliersAsync(CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<Supplier>>(c => c.Query<SupplierRow>("SELECT * FROM Supplier ORDER BY Name").Select(ToDomain).ToList(), ct);

    public Task SaveSupplierAsync(Supplier supplier, CancellationToken ct = default)
    {
        var now = clock.Now;
        if (supplier.CreatedAt == default) supplier.CreatedAt = now;
        supplier.UpdatedAt = now;
        var row = new SupplierRow
        {
            Id = supplier.Id, Name = supplier.Name, Phone = supplier.Phone, Gstin = supplier.Gstin, Notes = supplier.Notes,
            CreatedAtMs = Ms(supplier.CreatedAt), UpdatedAtMs = Ms(supplier.UpdatedAt),
        };
        return db.RunAsync(c => c.InsertOrReplace(row), ct);
    }

    public Task InsertPurchaseAsync(Purchase purchase, CancellationToken ct = default)
    {
        purchase.CreatedAt = clock.Now;
        purchase.UpdatedAt = purchase.CreatedAt;
        var row = new PurchaseRow
        {
            Id = purchase.Id, SupplierId = purchase.SupplierId, SupplierName = purchase.SupplierName, SupplierBillNo = purchase.SupplierBillNo,
            BillDay = Day(purchase.BillDate), PurchaseOrderId = purchase.PurchaseOrderId, TotalPaise = purchase.Total.Value,
            CreatedAtMs = Ms(purchase.CreatedAt),
        };
        var lines = purchase.Lines.Select((l, i) => new PurchaseLineRow
        {
            Id = l.Id, PurchaseId = purchase.Id, Position = i, ItemId = l.ItemId, TeluguName = l.TeluguName, EnglishName = l.EnglishName,
            Unit = (int)l.Unit, QtyMilli = l.Qty.Milli, CostPaise = l.CostPrice.Value, GstBasisPoints = (int)l.GstSlab,
            BatchNo = l.BatchNo, ExpiryDay = l.Expiry is { } e ? Day(e) : null, BatchId = l.BatchId, LineTotalPaise = l.LineTotal.Value,
        }).ToList();
        // Insert, never InsertOrReplace: a saved purchase is immutable; a second save of the same id fails loudly.
        return db.RunAsync(c => c.RunInTransactionIfNeeded(() =>
        {
            c.Insert(row);
            c.InsertAll(lines);
        }), ct);
    }

    public Task<Purchase?> GetPurchaseAsync(Guid id, CancellationToken ct = default)
        => db.RunAsync(c => c.Find<PurchaseRow>(id) is { } r
            ? ToDomain(r, c.Query<PurchaseLineRow>("SELECT * FROM PurchaseLine WHERE PurchaseId = ?", id))
            : null, ct);

    public Task<IReadOnlyList<Purchase>> ListPurchasesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<Purchase>>(c =>
        {
            var rows = c.Query<PurchaseRow>("SELECT * FROM Purchase WHERE BillDay >= ? AND BillDay <= ? ORDER BY BillDay DESC, CreatedAtMs DESC", Day(from), Day(to));
            var lines = new Dictionary<Guid, List<PurchaseLineRow>>();
            foreach (var chunk in rows.Chunk(BillRepository.LineQueryChunk))
                foreach (var l in c.Query<PurchaseLineRow>(
                             $"SELECT * FROM PurchaseLine WHERE PurchaseId IN ({string.Join(",", chunk.Select(_ => "?"))})",
                             chunk.Select(r => (object)r.Id).ToArray()))
                {
                    if (!lines.TryGetValue(l.PurchaseId, out var list)) lines[l.PurchaseId] = list = [];
                    list.Add(l);
                }
            return rows.Select(r => ToDomain(r, lines.TryGetValue(r.Id, out var l) ? l : [])).ToList();
        }, ct);

    public Task<bool> PurchaseBillExistsAsync(Guid supplierId, string supplierBillNo, CancellationToken ct = default)
        => db.RunAsync(c => c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Purchase WHERE SupplierId = ? AND SupplierBillNo = ? COLLATE NOCASE", supplierId, supplierBillNo.Trim()) > 0, ct);

    public Task<PurchaseOrder?> GetOrderAsync(Guid id, CancellationToken ct = default)
        => db.RunAsync(c => c.Find<PurchaseOrderRow>(id) is { } r ? ToDomain(r) : null, ct);

    public Task<IReadOnlyList<PurchaseOrder>> ListOrdersAsync(CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<PurchaseOrder>>(c => c.Query<PurchaseOrderRow>(
            "SELECT * FROM PurchaseOrder ORDER BY Status, UpdatedAtMs DESC").Select(ToDomain).ToList(), ct);

    public Task SaveOrderAsync(PurchaseOrder order, CancellationToken ct = default)
    {
        var now = clock.Now;
        if (order.CreatedAt == default) order.CreatedAt = now;
        order.UpdatedAt = now;
        var row = new PurchaseOrderRow
        {
            Id = order.Id, SupplierId = order.SupplierId, Status = (int)order.Status, PurchaseId = order.PurchaseId,
            LinesJson = JsonSerializer.Serialize(order.Lines.Select(l => new OrderLineJson(l.ItemId, l.Qty.Milli)).ToList(), Json),
            CreatedAtMs = Ms(order.CreatedAt), UpdatedAtMs = Ms(order.UpdatedAt),
        };
        return db.RunAsync(c => c.InsertOrReplace(row), ct);
    }

    public Task SetItemCostAsync(Guid itemId, Paise cost, CancellationToken ct = default)
        => db.RunAsync(c => c.Execute("UPDATE Item SET CostPaise = ? WHERE Id = ?", cost.Value, itemId), ct);

    private sealed record OrderLineJson(Guid ItemId, long QtyMilli);

    private static Supplier ToDomain(SupplierRow r) => new()
    {
        Id = r.Id, Name = r.Name, Phone = r.Phone, Gstin = r.Gstin, Notes = r.Notes, CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.UpdatedAtMs),
    };

    private static Purchase ToDomain(PurchaseRow r, IEnumerable<PurchaseLineRow> lines) => new()
    {
        Id = r.Id, SupplierId = r.SupplierId, SupplierName = r.SupplierName, SupplierBillNo = r.SupplierBillNo, BillDate = FromDay(r.BillDay),
        PurchaseOrderId = r.PurchaseOrderId, Total = new Paise(r.TotalPaise), CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.CreatedAtMs),
        Lines = lines.OrderBy(l => l.Position).Select(l => new PurchaseLine
        {
            Id = l.Id, ItemId = l.ItemId, TeluguName = l.TeluguName, EnglishName = l.EnglishName, Unit = (Unit)l.Unit,
            Qty = new Qty(l.QtyMilli), CostPrice = new Paise(l.CostPaise), GstSlab = (GstSlab)l.GstBasisPoints,
            BatchNo = l.BatchNo, Expiry = l.ExpiryDay is { } d ? FromDay(d) : null, BatchId = l.BatchId, LineTotal = new Paise(l.LineTotalPaise),
        }).ToList(),
    };

    private static PurchaseOrder ToDomain(PurchaseOrderRow r) => new()
    {
        Id = r.Id, SupplierId = r.SupplierId, Status = (PurchaseOrderStatus)r.Status, PurchaseId = r.PurchaseId,
        CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.UpdatedAtMs),
        Lines = (JsonSerializer.Deserialize<List<OrderLineJson>>(r.LinesJson, Json) ?? [])
            .Select(l => new PurchaseOrderLine(l.ItemId, new Qty(l.QtyMilli))).ToList(),
    };
}
