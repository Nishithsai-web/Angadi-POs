using System.Globalization;
using Angadi.Application.Persistence;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// H2-CORRECTION (ARCHITECT_CORRECTIONS): per-month counters "quota.count.YYYYMM" plus "quota.last_seen_ms".
/// Effective month = local month of max(now, last_seen) — a clock rolled back stays on the spent month;
/// a clock rolled forward spends a future bucket that keeps its own count.
/// </summary>
internal sealed class QuotaStore(AngadiDb db, Shops.ShopRegistry? shops = null) : IQuotaStore
{
    // FIX(SHOPS-QUOTA, 2026-11-01, v2-shops): Flaw: with several shops on one phone, each shop's database keeps its
    // own month buckets, so a phone whose Pro lapsed with N shops would get N x 50 free bills a month.
    // Fix: the free quota is per PHONE — "used this month" adds every other shop's used count for the same month
    // (its bucket, or its bills when the month has no bucket yet, plus its own M-3 unabsorbed lifetime bills), read
    // straight from that shop's file. Increment still touches only the open shop, so each bill is counted exactly
    // once, in its own shop, and each file's M-3 watermark/lifetime pair (S1-F2) stays consistent. Together the
    // per-file lifetime counters ARE the device-wide count. A shop file that cannot be read counts 0 (fails open,
    // like M-3's degrade path) — it must never stop the counter.
    // Why: one subscription covers every shop, so one free allowance must too. Logged in docs/AGENT_FIX_LOG.md.
    private int OtherShops(string month)
    {
        if (shops is null) return 0;
        long total = 0;
        foreach (var path in shops.OtherDatabasePaths(db.DatabasePath))
            total += UsedInFile(path, month);
        return (int)Math.Min(total, int.MaxValue / 2);
    }

    /// <summary>Another (closed) shop's used count for <paramref name="month"/>, read without changing that file.</summary>
    internal static int UsedInFile(string databasePath, string month)
    {
        if (!File.Exists(databasePath)) return 0;
        try
        {
            // FIX(QUOTA-RO, 2026-11-06, Claude): Flaw: another shop's database was opened read-WRITE just to count its bills,
            // so a quota read could create journal files in, lock or (on a driver fault) write to a shop that isn't open.
            // Fix: read-only. Why: a closed shop's books must not be touchable from the open shop. Logged in docs/AGENT_FIX_LOG.md.
            using var c = new SQLite.SQLiteConnection(databasePath, SQLite.SQLiteOpenFlags.ReadOnly | SQLite.SQLiteOpenFlags.FullMutex);
            c.BusyTimeout = TimeSpan.FromSeconds(2);
            var row = c.Find<MetaRow>(CountKey(month));
            var bucket = row is not null && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : BillsInMonth(c, month);
            var lifetime = Math.Max(LifetimeBillCounter.Read(databasePath), Watermark(c) ?? 0);
            return (int)Math.Min(bucket + (long)Unabsorbed(c, lifetime), int.MaxValue / 4);
        }
        catch (Exception ex) when (ex is SQLite.SQLiteException or IOException or UnauthorizedAccessException)
        {
            return 0;   // degrade like M-3: a quota read must never stop the counter
        }
    }

    private static int BillsInMonth(SQLite.SQLiteConnection c, string month)
    {
        var y = int.Parse(month[..4], CultureInfo.InvariantCulture);
        var m = int.Parse(month[4..], CultureInfo.InvariantCulture);
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM Bill WHERE Day >= ? AND Day <= ? AND Status <> 0", y * 10000 + m * 100 + 1, y * 10000 + m * 100 + 31);
    }

    private const string LastSeenKey = "quota.last_seen_ms";
    internal const string WatermarkKey = "quota.lifetime_watermark";

    // FIX(M-3, 2026-10-20, FIX-2): Flaw: every quota counter lived inside the DB file, so replacing the
    // file outside the app (file-manager restore, support-side swap) rewound the free-tier count together
    // with the books — the reinstall/restore reset the owner accepted, with a cheap mitigation (owner
    // decision, 2026-10-20). Fix: a monotonic lifetime finalized-bill counter in a SIBLING FILE
    // ("<db>.lifetime" — app-private storage with the same survival profile as platform Preferences:
    // survives a DB swap, wiped by uninstall/clear-data, which stays accepted). The DB carries a
    // watermark = the lifetime value it has absorbed. Exact semantics:
    //   used(month) = bucket(month) + max(0, lifetime − watermark)
    // In-app restores never double-count: BackupService raises the watermark to the device lifetime when
    // it writes back the max buckets (RESTORE-1). A backup restored on a DIFFERENT device has
    // lifetime ≤ watermark → no floor. A DB with no watermark is grandfathered (watermark := lifetime at
    // first increment) so existing installs are never suddenly floored by their own history.
    // Why so conservative: same-shop lineage across a file swap is unknowable — this only ever stops the
    // count going DOWN after an out-of-band rewind on the same device. Logged in docs/AGENT_FIX_LOG.md.
    public Task<int> UsedThisMonthAsync(DateTimeOffset now, CancellationToken ct = default) =>
        db.RunAsync(c =>
        {
            var month = EffectiveMonth(c, now, advance: false);
            return Count(c, month) + Unabsorbed(c, SyncedLifetime(c)) + OtherShops(month);
        }, ct);

    public Task IncrementAsync(DateTimeOffset now, CancellationToken ct = default) =>
        db.RunAsync(c =>
        {
            var month = EffectiveMonth(c, now, advance: true);
            var lifetime = SyncedLifetime(c);
            var watermark = Watermark(c) ?? lifetime;   // M-3: no watermark yet → grandfather (see above)
            var used = Count(c, month) + Math.Max(0, lifetime - watermark);
            c.InsertOrReplace(new MetaRow { Key = CountKey(month), Value = (used + 1).ToString(CultureInfo.InvariantCulture) });
            // FIX(S1-F2, 2026-10-22, S1): Flaw: the sibling ".lifetime" file was written HERE, inside the finalize
            // transaction but outside SQLite — a commit that then failed (storage full) rolled the watermark back while
            // the file stayed advanced, so every later read counted a phantom bill (a free bill lost per failed save).
            // Fix: this bill is recorded only in the DB (watermark = lifetime + 1, committed or rolled back with the
            // bill); the file is caught up from the COMMITTED watermark at the start of every later read/increment
            // (SyncedLifetime). Why: M-3's floor stays (an out-of-band rewind still cannot lower the count below what
            // the file saw), and a rolled-back bill can never count. Logged in docs/AGENT_FIX_LOG.md.
            c.InsertOrReplace(new MetaRow { Key = WatermarkKey, Value = (lifetime + 1).ToString(CultureInfo.InvariantCulture) });
        }, ct);

    /// <summary>
    /// S1-F2: the device counter, caught up to the DB's committed watermark (max of the two, written back when the DB
    /// is ahead). Called before this operation writes anything, so the watermark it sees is a committed one.
    /// </summary>
    private long SyncedLifetime(SQLite.SQLiteConnection c)
    {
        var lifetime = LifetimeBillCounter.Read(db.DatabasePath);
        if (Watermark(c) is { } watermark && watermark > lifetime)
        {
            LifetimeBillCounter.Write(db.DatabasePath, watermark);
            lifetime = watermark;
        }
        return lifetime;
    }

    /// <summary>M-3: bills this device finalized that the current DB file has never absorbed.</summary>
    private static int Unabsorbed(SQLite.SQLiteConnection c, long lifetime)
    {
        var watermark = Watermark(c);
        if (watermark is null) return 0;   // grandfathered until the first increment stamps it
        var diff = lifetime - watermark.Value;
        return diff > 0 ? (int)Math.Min(diff, int.MaxValue) : 0;
    }

    private static long? Watermark(SQLite.SQLiteConnection c)
    {
        var row = c.Find<MetaRow>(WatermarkKey);
        return row is not null && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // FIX(QUOTA-1, 2026-10-20, FIX-2): Flaw: last_seen was honoured without any bound. One accidental
    // far-future clock jump (clock set a year ahead, one bill sold, clock corrected) left last_seen a year
    // ahead, so that future month's bucket — and its 50-bill limit — governed the shop for a whole year.
    // Fix: a last_seen more than 48 h ahead of now is treated as poison: ignored for month selection, and
    // repaired to now on the next increment so it does not persist. Why: skew/rollback up to 48 h still
    // pins the spent month (H2); beyond that, a year-long paywall costs the shop more than the bypass it
    // prevents. TRADEOFF: a rollback longer than 48 h no longer pins by last_seen alone — months with real
    // bills stay protected because Count() seeds from the Bill table. Logged in docs/AGENT_FIX_LOG.md.
    private static readonly long MaxFutureSkewMs = (long)TimeSpan.FromHours(48).TotalMilliseconds;

    private static string EffectiveMonth(SQLite.SQLiteConnection c, DateTimeOffset now, bool advance)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var row = c.Find<MetaRow>(LastSeenKey);
        var lastSeen = row is not null && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var poisoned = lastSeen > nowMs + MaxFutureSkewMs;   // QUOTA-1: see MaxFutureSkewMs above
        var effectiveMs = poisoned ? nowMs : Math.Max(nowMs, lastSeen);
        if (advance && (nowMs > lastSeen || poisoned))
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

/// <summary>M-3: the device-lifetime finalized-bill counter, kept in a sibling file beside the database
/// so replacing the DB file cannot rewind it (same survival profile as platform Preferences: survives a
/// DB swap, wiped by uninstall/clear-data). Plain text, one integer. Any I/O failure degrades to 0 —
/// the pre-M-3 DB-only behaviour — and never breaks billing.</summary>
internal static class LifetimeBillCounter
{
    public static string PathFor(string databasePath) => databasePath + ".lifetime";

    public static long Read(string databasePath)
    {
        try
        {
            var path = PathFor(databasePath);
            return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static void Write(string databasePath, long value)
    {
        try { File.WriteAllText(PathFor(databasePath), value.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* degrade: DB-only quota */ }
    }
}
