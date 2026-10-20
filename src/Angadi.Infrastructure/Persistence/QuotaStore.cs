using System.Globalization;
using Angadi.Application.Persistence;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// H2-CORRECTION (ARCHITECT_CORRECTIONS): per-month counters "quota.count.YYYYMM" plus "quota.last_seen_ms".
/// Effective month = local month of max(now, last_seen) — a clock rolled back stays on the spent month;
/// a clock rolled forward spends a future bucket that keeps its own count.
/// </summary>
internal sealed class QuotaStore(AngadiDb db) : IQuotaStore
{
    private const string LastSeenKey = "quota.last_seen_ms";

    public Task<int> UsedThisMonthAsync(DateTimeOffset now, CancellationToken ct = default) =>
        db.RunAsync(c =>
        {
            var month = EffectiveMonth(c, now, advance: false);
            return Count(c, month);
        }, ct);

    public Task IncrementAsync(DateTimeOffset now, CancellationToken ct = default) =>
        db.RunAsync(c =>
        {
            var month = EffectiveMonth(c, now, advance: true);
            var used = Count(c, month);
            c.InsertOrReplace(new MetaRow { Key = CountKey(month), Value = (used + 1).ToString(CultureInfo.InvariantCulture) });
        }, ct);

    private static string EffectiveMonth(SQLite.SQLiteConnection c, DateTimeOffset now, bool advance)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var row = c.Find<MetaRow>(LastSeenKey);
        var lastSeen = row is not null && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var effectiveMs = Math.Max(nowMs, lastSeen);
        if (advance && nowMs > lastSeen)
            c.InsertOrReplace(new MetaRow { Key = LastSeenKey, Value = nowMs.ToString(CultureInfo.InvariantCulture) });
        var local = DateTimeOffset.FromUnixTimeMilliseconds(effectiveMs).ToOffset(now.Offset);
        return local.ToString("yyyyMM", CultureInfo.InvariantCulture);
    }

    private static string CountKey(string month) => "quota.count." + month;

    /// <summary>First use of a month on an existing install: seed from the bills table, so upgrading never resets the count.</summary>
    private static int Count(SQLite.SQLiteConnection c, string month)
    {
        var row = c.Find<MetaRow>(CountKey(month));
        if (row is not null && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        var y = int.Parse(month[..4], CultureInfo.InvariantCulture);
        var m = int.Parse(month[4..], CultureInfo.InvariantCulture);
        var seeded = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Bill WHERE Day >= ? AND Day <= ? AND Status <> 0", y * 10000 + m * 100 + 1, y * 10000 + m * 100 + 31);
        // Persist the seed now: finalize checks first, saves the bill, then increments — re-seeding at
        // increment time would count the just-saved bill twice.
        c.InsertOrReplace(new MetaRow { Key = CountKey(month), Value = seeded.ToString(CultureInfo.InvariantCulture) });
        return seeded;
    }
}
