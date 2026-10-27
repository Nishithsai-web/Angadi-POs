using System.Globalization;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using static Angadi.Infrastructure.Persistence.Mapping;

namespace Angadi.Infrastructure.Persistence;

/// <summary>v2 returns: SalesReturn + ReturnLine rows. Numbers follow the bill rule (prefix, per-device sequence, D6).</summary>
internal sealed class ReturnRepository(AngadiDb db, IClock clock) : IReturnRepository
{
    /// <summary>
    /// The next return sequence, in Meta. The "seq." prefix makes a restore carry it forward like the bill sequence
    /// (BackupService RESTORE-1): return numbers already handed out are never reused.
    /// </summary>
    internal const string SequenceKey = "seq.return";

    public Task<SalesReturn?> GetAsync(Guid id, CancellationToken ct = default)
        => db.RunAsync(c => c.Find<SalesReturnRow>(id) is { } r
            ? PaymentMapping.ToDomain(r, c.Query<ReturnLineRow>("SELECT * FROM ReturnLine WHERE ReturnId = ?", id))
            : null, ct);

    public Task<IReadOnlyList<SalesReturn>> ListByBillAsync(Guid billId, CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<SalesReturn>>(c => Load(c,
            c.Query<SalesReturnRow>("SELECT * FROM SalesReturn WHERE BillId = ? ORDER BY ReturnedAtMs, Number", billId)), ct);

    public Task<IReadOnlyList<SalesReturn>> ListByDayAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        => db.RunAsync<IReadOnlyList<SalesReturn>>(c => Load(c,
            c.Query<SalesReturnRow>("SELECT * FROM SalesReturn WHERE Day >= ? AND Day <= ? ORDER BY ReturnedAtMs, Number", Day(from), Day(to))), ct);

    public Task<string> NextReturnNumberAsync(CancellationToken ct = default)
        => db.RunAsync(c =>
        {
            var shop = c.Table<ShopProfileRow>().FirstOrDefault()
                       ?? throw new InvalidOperationException("Shop profile must exist before a return (FR-01).");
            // "R" + the bill prefix; the default bill prefix "A" is left out, so a single-phone shop reads R-000001.
            var billPrefix = string.IsNullOrWhiteSpace(shop.BillPrefix) ? "" : shop.BillPrefix.Trim();
            var prefix = billPrefix.Length == 0 || string.Equals(billPrefix, "A", StringComparison.Ordinal) ? "R" : "R" + billPrefix;
            var stored = c.Find<MetaRow>(SequenceKey);
            var seq = stored is not null && long.TryParse(stored.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1;
            string number;
            // Defence in depth: never hand out a number that is already on a return in this database.
            while (c.ExecuteScalar<int>("SELECT COUNT(*) FROM SalesReturn WHERE Number = ?", number = $"{prefix}-{seq:D6}") > 0) seq++;
            c.InsertOrReplace(new MetaRow { Key = SequenceKey, Value = (seq + 1).ToString(CultureInfo.InvariantCulture) });
            return number;
        }, ct);

    public Task SaveAsync(SalesReturn ret, CancellationToken ct = default)
    {
        if (ret.CreatedAt == default) ret.CreatedAt = clock.Now;
        ret.UpdatedAt = ret.CreatedAt;
        if (ret.ReturnedAt == default) ret.ReturnedAt = ret.CreatedAt;
        var row = PaymentMapping.ToRow(ret);
        var lines = ret.Lines.Select((l, i) => PaymentMapping.ToRow(l, ret.Id, i)).ToList();
        return db.RunAsync(c => c.RunInTransactionIfNeeded(() =>
        {
            c.Insert(row);   // insert-only: a recorded return is never rewritten
            c.InsertAll(lines);
        }), ct);
    }

    private static List<SalesReturn> Load(SQLite.SQLiteConnection c, List<SalesReturnRow> rows)
    {
        if (rows.Count == 0) return [];
        var lines = new Dictionary<Guid, List<ReturnLineRow>>();
        foreach (var chunk in rows.Chunk(BillRepository.LineQueryChunk))
        {
            var found = c.Query<ReturnLineRow>(
                $"SELECT * FROM ReturnLine WHERE ReturnId IN ({string.Join(",", chunk.Select(_ => "?"))})",
                chunk.Select(r => (object)r.Id).ToArray());
            foreach (var l in found)
            {
                if (!lines.TryGetValue(l.ReturnId, out var list)) lines[l.ReturnId] = list = [];
                list.Add(l);
            }
        }
        return rows.Select(r => PaymentMapping.ToDomain(r, lines.TryGetValue(r.Id, out var l) ? l : [])).ToList();
    }
}
