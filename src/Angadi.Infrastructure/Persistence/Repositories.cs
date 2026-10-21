using System.Text.Json;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Events;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using static Angadi.Infrastructure.Persistence.Mapping;

namespace Angadi.Infrastructure.Persistence;

internal sealed class ItemRepository(AngadiDb db, IClock clock) : IItemRepository
{
    public Task<Item?> GetAsync(Guid id, CancellationToken ct = default)
        => db.RunAsync(c => c.Find<ItemRow>(id) is { } r ? ToDomain(r) : null, ct);

    public Task<Item?> GetByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        if (Barcodes.Normalize(barcode) is not { } code) return Task.FromResult<Item?>(null);
        var alt = Barcodes.Alternate(code) ?? code;   // rows saved before normalization may hold the 12-digit UPC-A form
        return db.RunAsync(c => c.Query<ItemRow>("SELECT * FROM Item WHERE Barcode IN (?, ?) LIMIT 1", code, alt).FirstOrDefault() is { } r ? ToDomain(r) : null, ct);
    }

    public Task<IReadOnlyList<Item>> SearchAsync(string text, int limit = 20, CancellationToken ct = default)
    {
        var q = text.Trim();
        if (q.Length == 0) return Task.FromResult<IReadOnlyList<Item>>([]);
        var like = "%" + q.Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal) + "%";
        return db.RunAsync<IReadOnlyList<Item>>(c => c.Query<ItemRow>(
                "SELECT * FROM Item WHERE IsActive = 1 AND (TeluguName LIKE ? OR EnglishName LIKE ? OR AliasesJson LIKE ? OR Barcode LIKE ?) " +
                "ORDER BY TeluguName LIMIT ?", like, like, like, q + "%", limit)
            .Select(ToDomain).ToList(), ct);
    }

    public Task<IReadOnlyList<Item>> GetActiveAsync(CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<Item>>(c => c.Query<ItemRow>("SELECT * FROM Item WHERE IsActive = 1").Select(ToDomain).ToList(), ct);

    public Task<IReadOnlyList<Item>> GetQuickSaleAsync(CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<Item>>(c => c.Query<ItemRow>("SELECT * FROM Item WHERE IsActive = 1 AND IsQuickSale = 1")
            .Select(ToDomain).OrderBy(i => i.TeluguName, StringComparer.Ordinal).ToList(), ct);

    public Task<IReadOnlyList<Item>> GetLowStockAsync(CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<Item>>(c => c.Query<ItemRow>(
                "SELECT * FROM Item WHERE IsActive = 1 AND StockMilli <= LowStockMilli ORDER BY StockMilli")
            .Select(ToDomain).ToList(), ct);

    public Task SaveAsync(Item item, CancellationToken ct = default)
    {
        var now = clock.Now;
        if (item.CreatedAt == default) item.CreatedAt = now;
        // DATA-3: how fresh is the caller's copy? Captured BEFORE the overwrite below — a copy loaded
        // before another writer saved carries the older UpdatedAt and is stale by definition.
        var loadedUpdatedAtMs = item.UpdatedAt.ToUnixTimeMilliseconds();
        item.UpdatedAt = now;
        var row = ToRow(item);
        // Stock is owned by IStockLedger: never overwrite it from a stale item object.
        return db.RunAsync(c =>
        {
            var existing = c.Find<ItemRow>(row.Id);
            if (existing is null) { c.Insert(row); return; }
            row.StockMilli = existing.StockMilli;
            // FIX(DATA-3, 2026-10-20, FIX-2): Flaw: only stock was protected — a flow holding an Item
            // across an AliasLearner write (the learner loads fresh, appends, saves) saved its stale copy
            // and silently reverted LearnedAliasesJson: the voice learning evaporated. Fix: same pattern
            // as stock — when the caller's copy is stale (its UpdatedAt predates the stored row), the
            // stored learned aliases win. The learner itself always saves a fresh copy, so its appends,
            // confidence bumps, demotions and trims write through untouched; manual AliasesJson stays
            // last-writer-wins (the item editor owns it). Why: learned aliases are accumulated shop data,
            // exactly like stock — no stale screen may rewind them. Logged in docs/AGENT_FIX_LOG.md.
            if (loadedUpdatedAtMs != existing.UpdatedAtMs) row.LearnedAliasesJson = existing.LearnedAliasesJson;
            c.Update(row);
            item.Stock = new Qty(existing.StockMilli);
        }, ct);
    }

    public Task<bool> AnyAsync(CancellationToken ct = default)
        => db.RunAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Item") > 0, ct);
}

internal sealed class BillRepository(AngadiDb db, IClock clock) : IBillRepository
{
    public Task<Bill?> GetAsync(Guid id, CancellationToken ct = default)
        => db.RunAsync(c =>
        {
            var row = c.Find<BillRow>(id);
            return row is null ? null : ToDomain(row, c.Query<BillLineRow>("SELECT * FROM BillLine WHERE BillId = ?", id));
        }, ct);

    public Task<IReadOnlyList<Bill>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var a = Ms(from);
        var b = Ms(to);
        return db.RunAsync<IReadOnlyList<Bill>>(c =>
        {
            var rows = c.Query<BillRow>("SELECT * FROM Bill WHERE FinalizedAtMs >= ? AND FinalizedAtMs < ? ORDER BY FinalizedAtMs DESC", a, b);
            return Load(c, rows);
        }, ct);
    }

    /// <summary>Bills finalized on local days [from, to] inclusive — what reports use.</summary>
    public Task<IReadOnlyList<Bill>> ListByDayAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<Bill>>(c => Load(c,
            c.Query<BillRow>("SELECT * FROM Bill WHERE Day >= ? AND Day <= ? AND Status <> 0 ORDER BY FinalizedAtMs DESC", Day(from), Day(to))), ct);

    public Task<IReadOnlyList<Bill>> ListByCustomerAsync(Guid customerId, int limit, CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<Bill>>(c => Load(c,
            c.Query<BillRow>("SELECT * FROM Bill WHERE CustomerId = ? AND Status <> 0 ORDER BY FinalizedAtMs DESC LIMIT ?", customerId, limit)), ct);

    internal const int LineQueryChunk = 500;

    private static List<Bill> Load(SQLite.SQLiteConnection c, List<BillRow> rows)
    {
        if (rows.Count == 0) return [];
        // One "?" per bill would break past SQLite's host-parameter limit (999 on older builds) on a busy month:
        // fetch the lines in chunks instead.
        var lines = new Dictionary<Guid, List<BillLineRow>>();
        foreach (var chunk in rows.Chunk(LineQueryChunk))
        {
            var found = c.Query<BillLineRow>(
                $"SELECT * FROM BillLine WHERE BillId IN ({string.Join(",", chunk.Select(_ => "?"))})",
                chunk.Select(r => (object)r.Id).ToArray());
            foreach (var l in found)
            {
                if (!lines.TryGetValue(l.BillId, out var list)) lines[l.BillId] = list = [];
                list.Add(l);
            }
        }
        return rows.Select(r => ToDomain(r, lines.TryGetValue(r.Id, out var l) ? l : [])).ToList();
    }

    public Task<int> CountFinalizedInMonthAsync(int year, int month, CancellationToken ct = default)
    {
        var from = year * 10000 + month * 100 + 1;
        var to = year * 10000 + month * 100 + 31;
        // Voided bills still count: the quota is for bills made, so void + re-bill can't dodge it.
        return db.RunAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Bill WHERE Day >= ? AND Day <= ? AND Status <> 0", from, to), ct);
    }

    public Task SaveAsync(Bill bill, CancellationToken ct = default)
    {
        var now = clock.Now;
        if (bill.CreatedAt == default) bill.CreatedAt = now;
        bill.UpdatedAt = now;
        var row = ToRow(bill);
        var lines = bill.Lines.Select((l, i) => ToRow(l, bill.Id, i)).ToList();
        return db.RunAsync(c =>
        {
            c.RunInTransactionIfNeeded(() =>
            {
                c.InsertOrReplace(row);
                c.Execute("DELETE FROM BillLine WHERE BillId = ?", bill.Id);
                c.InsertAll(lines);
            });
        }, ct);
    }
}

internal sealed class CustomerRepository(AngadiDb db, IClock clock) : ICustomerRepository
{
    /// <summary>A kirana's regulars fit on one scrollable list; typing narrows past this.</summary>
    internal const int CustomerSearchLimit = 50;

    public Task<Customer?> GetAsync(Guid id, CancellationToken ct = default)
        => db.RunAsync(c => c.Find<CustomerRow>(id) is { } r ? ToDomain(r) : null, ct);

    public Task<IReadOnlyList<Customer>> SearchAsync(string text, CancellationToken ct = default)
    {
        var like = "%" + text.Trim().Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal) + "%";
        return db.RunAsync<IReadOnlyList<Customer>>(c => c.Query<CustomerRow>(
                "SELECT * FROM Customer WHERE Name LIKE ? OR Phone LIKE ? ORDER BY Name LIMIT ?", like, like, CustomerSearchLimit)
            .Select(ToDomain).ToList(), ct);
    }

    public Task<IReadOnlyList<Customer>> GetWithDuesAsync(CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<Customer>>(c => c.Query<CustomerRow>(
                "SELECT * FROM Customer WHERE DuesBalancePaise <> 0 ORDER BY DuesBalancePaise DESC")
            .Select(ToDomain).ToList(), ct);

    public Task<IReadOnlyList<DuesEntry>> GetLedgerAsync(Guid customerId, CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<DuesEntry>>(c => c.Query<DuesEntryRow>(
                "SELECT * FROM DuesEntry WHERE CustomerId = ? ORDER BY CreatedAtMs DESC", customerId)
            .Select(ToDomain).ToList(), ct);

    // FIX(S4-4, 2026-10-21, S4): Flaw: the counter's dues banner read the full udhaar ledger of every owing customer,
    // one query each (N+1) — ~26k rows for a busy year, most of them payments (87-120 ms on CI, every time the counter
    // opens, holding the database gate a scan would wait on). Fix: ONE pass: charges and reversals as recorded (aging
    // needs their dates and bills), payments and write-offs summed per customer in SQL and handed over as one Payment
    // entry — DuesAging only ever adds them into the settlement pool, so the age is identical. Why: the banner is the
    // last load before the counter is usable again. Logged in docs/AGENT_FIX_LOG.md.
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<DuesEntry>>> GetOwingAgingLedgersAsync(CancellationToken ct = default)
        => db.RunAsync<IReadOnlyDictionary<Guid, IReadOnlyList<DuesEntry>>>(c =>
        {
            const string Owing = "CustomerId IN (SELECT Id FROM Customer WHERE DuesBalancePaise > 0)";
            var ledgers = c.Query<DuesEntryRow>(
                    $"SELECT * FROM DuesEntry WHERE Kind IN (?, ?) AND {Owing} ORDER BY CreatedAtMs DESC",
                    (int)DuesEntryKind.Charge, (int)DuesEntryKind.Reversal)
                .GroupBy(r => r.CustomerId)
                .ToDictionary(g => g.Key, g => g.Select(ToDomain).ToList());
            foreach (var paid in c.Query<SettledRow>(
                         $"SELECT CustomerId, SUM(AmountPaise) AS AmountPaise FROM DuesEntry WHERE Kind IN (?, ?) AND {Owing} GROUP BY CustomerId",
                         (int)DuesEntryKind.Payment, (int)DuesEntryKind.WriteOff))
            {
                if (!ledgers.TryGetValue(paid.CustomerId, out var ledger)) ledgers[paid.CustomerId] = ledger = [];
                ledger.Add(new DuesEntry
                {
                    Id = Guid.Empty, CustomerId = paid.CustomerId, Kind = DuesEntryKind.Payment, Amount = new Paise(paid.AmountPaise),
                    CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
                });
            }
            return ledgers.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<DuesEntry>)kv.Value);
        }, ct);

    private sealed class SettledRow
    {
        public Guid CustomerId { get; set; }
        public long AmountPaise { get; set; }
    }

    public Task SaveAsync(Customer customer, CancellationToken ct = default)
    {
        var now = clock.Now;
        if (customer.CreatedAt == default) customer.CreatedAt = now;
        customer.UpdatedAt = now;
        var row = ToRow(customer);
        // The balance is owned by the ledger: keep the stored value on edits.
        return db.RunAsync(c =>
        {
            var existing = c.Find<CustomerRow>(row.Id);
            if (existing is null) { c.Insert(row); return; }
            row.DuesBalancePaise = existing.DuesBalancePaise;
            c.Update(row);
            customer.DuesBalance = new Paise(existing.DuesBalancePaise);
        }, ct);
    }

    /// <summary>Writes the ledger row and moves the balance atomically (Charge +, Payment/Reversal/WriteOff −; SEC-5).</summary>
    public Task AddDuesEntryAsync(DuesEntry entry, CancellationToken ct = default)
    {
        if (entry.CreatedAt == default) entry.CreatedAt = clock.Now;
        entry.UpdatedAt = entry.CreatedAt;
        var signed = entry.Kind == DuesEntryKind.Charge ? entry.Amount.Value : -entry.Amount.Value;
        var row = ToRow(entry);
        return db.RunAsync(c => c.RunInTransactionIfNeeded(() =>
        {
            c.Insert(row);
            c.Execute("UPDATE Customer SET DuesBalancePaise = DuesBalancePaise + ?, UpdatedAtMs = ? WHERE Id = ?",
                signed, row.CreatedAtMs, entry.CustomerId);
        }), ct);
    }
}

internal sealed class StockLedger(AngadiDb db, IClock clock) : IStockLedger
{
    public Task AdjustAsync(Guid itemId, Qty delta, StockMoveReason reason, Guid? billId = null, string? note = null, CancellationToken ct = default)
    {
        var now = clock.Now;
        var row = new StockMoveRow
        {
            Id = Guid.NewGuid(), ItemId = itemId, DeltaMilli = delta.Milli, Reason = (int)reason, BillId = billId, Note = note,
            Day = Day(now), CreatedAtMs = Ms(now),
        };
        // Relative UPDATE, never read-modify-write: concurrent adjusts cannot lose an update (NFR-06).
        return db.RunAsync(c => c.RunInTransactionIfNeeded(() =>
        {
            var changed = c.Execute("UPDATE Item SET StockMilli = StockMilli + ?, UpdatedAtMs = ? WHERE Id = ?", delta.Milli, row.CreatedAtMs, itemId);
            if (changed != 1) throw new InvalidOperationException($"Stock adjust for unknown item {itemId}");
            c.Insert(row);
        }), ct);
    }

    public Task<IReadOnlyList<StockMove>> GetHistoryAsync(Guid itemId, CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<StockMove>>(c => c.Query<StockMoveRow>(
                "SELECT * FROM StockMove WHERE ItemId = ? ORDER BY CreatedAtMs DESC", itemId)
            .Select(ToDomain).ToList(), ct);

    public Task<IReadOnlyList<DailySales>> GetDailySalesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<DailySales>>(c => c.Query<DailySalesRow>(
                "SELECT ItemId, Day, -SUM(DeltaMilli) AS SoldMilli FROM StockMove WHERE Reason IN (?, ?) AND Day >= ? AND Day <= ? GROUP BY ItemId, Day",
                (int)StockMoveReason.Sale, (int)StockMoveReason.VoidRestore, Day(from), Day(to))
            .Select(r => new DailySales(r.ItemId, FromDay(r.Day), new Qty(r.SoldMilli))).ToList(), ct);

    // FIX(S4-2, 2026-10-21, S4): Flaw: dead stock loaded a year of daily sales (~200k rows for a busy shop) into C# to
    // find one "last sold" day per item (270 ms on CI). Fix: aggregated in SQL; "+Day" keeps the planner off the Day
    // index (the range is the whole year), so it walks the S4-1 covering index (ItemId, Day, Reason, DeltaMilli) in
    // (item, day) order with no table reads and no temp sort of the inner groups. Same rule as before: a day counts
    // when its Sale + VoidRestore moves net to a sale (a bill voided the same day does not). Why: the Reports screen
    // runs this on every open. Logged in docs/AGENT_FIX_LOG.md.
    public Task<IReadOnlyDictionary<Guid, DateOnly>> LastSoldDaysAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        => db.RunAsync<IReadOnlyDictionary<Guid, DateOnly>>(c => c.Query<DailySalesRow>(
                "SELECT ItemId, MAX(Day) AS Day, 0 AS SoldMilli FROM (" +
                "SELECT ItemId, Day FROM StockMove WHERE Reason IN (?, ?) AND +Day >= ? AND +Day <= ? GROUP BY ItemId, Day HAVING SUM(DeltaMilli) < 0" +
                ") GROUP BY ItemId",
                (int)StockMoveReason.Sale, (int)StockMoveReason.VoidRestore, Day(from), Day(to))
            .ToDictionary(r => r.ItemId, r => FromDay(r.Day)), ct);

    public Task<IReadOnlyList<StockMove>> ListMovesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<StockMove>>(c => c.Query<StockMoveRow>(
                "SELECT * FROM StockMove WHERE Day >= ? AND Day <= ? ORDER BY CreatedAtMs DESC", Day(from), Day(to))
            .Select(ToDomain).ToList(), ct);

    public Task<IReadOnlyDictionary<Guid, long>> LedgerTotalsAsync(CancellationToken ct = default)
        => db.RunAsync<IReadOnlyDictionary<Guid, long>>(c => c.Query<LedgerTotalRow>(
                "SELECT ItemId, SUM(DeltaMilli) AS TotalMilli FROM StockMove GROUP BY ItemId")
            .ToDictionary(r => r.ItemId, r => r.TotalMilli), ct);

    private sealed class LedgerTotalRow
    {
        public Guid ItemId { get; set; }
        public long TotalMilli { get; set; }
    }

    private sealed class DailySalesRow
    {
        public Guid ItemId { get; set; }
        public int Day { get; set; }
        public long SoldMilli { get; set; }
    }
}

internal sealed class NoteRepository(AngadiDb db, IClock clock) : INoteRepository
{
    public Task<IReadOnlyList<Angadi.Domain.Notes.Note>> ListAsync(CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<Angadi.Domain.Notes.Note>>(c => c.Query<NoteRow>(
                "SELECT * FROM Note ORDER BY UpdatedAtMs DESC").Select(ToDomain).ToList(), ct);

    public Task<Angadi.Domain.Notes.Note?> GetAsync(Guid id, CancellationToken ct = default)
        => db.RunAsync(c => c.Find<NoteRow>(id) is { } r ? ToDomain(r) : null, ct);

    public Task SaveAsync(Angadi.Domain.Notes.Note note, CancellationToken ct = default)
    {
        var now = clock.Now;
        if (note.CreatedAt == default) note.CreatedAt = now;
        note.UpdatedAt = now;
        return db.RunAsync(c => c.InsertOrReplace(ToRow(note)), ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
        => db.RunAsync(c => c.Delete<NoteRow>(id), ct);
}

internal sealed class Outbox(AngadiDb db, IClock clock) : IOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task AppendAsync(string aggregateType, IDomainEvent @event, CancellationToken ct = default)
    {
        var row = new OutboxRow
        {
            Id = Guid.NewGuid(), AggregateType = aggregateType, AggregateId = @event.AggregateId,
            EventType = @event.GetType().Name, PayloadJson = JsonSerializer.Serialize(@event, @event.GetType(), Json),
            CreatedAtMs = Ms(clock.Now), SyncState = 0,
        };
        return db.RunAsync(c => c.Insert(row), ct);
    }

    // FIX(M-11, 2026-10-20, FIX-4): the retention sweep behind IOutbox.PruneOlderThanAsync — one range DELETE
    // on the append timestamp; see the interface for the flaw and the 90-day rationale.
    public Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => db.RunAsync(c => c.Execute("DELETE FROM Outbox WHERE CreatedAtMs < ?", Ms(cutoff)), ct);
}

internal sealed class ShopProfileRepository(AngadiDb db, IClock clock) : IShopProfileRepository
{
    public Task<ShopProfile?> GetAsync(CancellationToken ct = default)
        => db.RunAsync(c => c.Table<ShopProfileRow>().FirstOrDefault() is { } r ? ToDomain(r) : null, ct);

    public Task SaveAsync(ShopProfile profile, CancellationToken ct = default)
    {
        var now = clock.Now;
        if (profile.CreatedAt == default) profile.CreatedAt = now;
        profile.UpdatedAt = now;
        var row = ToRow(profile);
        return db.RunAsync(c =>
        {
            var existing = c.Table<ShopProfileRow>().FirstOrDefault();
            if (existing is null) { c.Insert(row); return; }
            row.Id = existing.Id;   // exactly one profile row
            row.NextBillSequence = existing.NextBillSequence;   // owned by NextBillNumberAsync
            c.Update(row);
            profile.Id = existing.Id;
            profile.NextBillSequence = existing.NextBillSequence;
        }, ct);
    }

    public Task<string> NextBillNumberAsync(CancellationToken ct = default)
        => db.RunAsync(c =>
        {
            var row = c.Table<ShopProfileRow>().FirstOrDefault()
                      ?? throw new InvalidOperationException("Shop profile must exist before billing (FR-01).");
            var seq = row.NextBillSequence;
            c.Execute("UPDATE ShopProfile SET NextBillSequence = NextBillSequence + 1 WHERE Id = ?", row.Id);
            return $"{row.BillPrefix}-{seq:D6}";
        }, ct);
}

internal static class SqliteExtensions
{
    /// <summary>Uses the caller's transaction when one is open; otherwise wraps the action in its own.</summary>
    public static void RunInTransactionIfNeeded(this SQLite.SQLiteConnection c, Action action)
    {
        if (c.IsInTransaction) action();
        else c.RunInTransaction(action);
    }
}
