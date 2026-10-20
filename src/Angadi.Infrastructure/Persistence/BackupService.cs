using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Microsoft.Extensions.Logging;
using SQLite;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// FR-37. A backup is a copy of the SQLite file after a WAL checkpoint. Restore checks that the file
/// is an Angadi database with a schema version this build understands before swapping it in.
/// </summary>
internal sealed class BackupService(AngadiDb db, IClock clock, string exportDirectory, ILogger<BackupService> log) : IBackupService
{
    public Task<Result<string>> ExportAsync(CancellationToken ct = default) => ExportCoreAsync(overwrite: true, ct);

    // FIX(DATA-1b, 2026-10-20, FIX-2): Flaw: export names had only minute precision and exports overwrote
    // freely — two exports inside one minute (a manual one and the restore safety copy) silently replaced
    // each other, so the "safety" copy could end up being the very file it was meant to protect against.
    // Fix: names carry seconds plus a GUID suffix, and the restore safety copy is written with
    // overwrite: false. Why: a backup that the next backup can silently replace is not a safety copy.
    // Logged in docs/AGENT_FIX_LOG.md.
    private async Task<Result<string>> ExportCoreAsync(bool overwrite, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(exportDirectory);
            var path = Path.Combine(exportDirectory, $"angadi-backup-{clock.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.db");
            // SEC-6: the device HMAC key lives in the live DB's Meta (created once); exports inherit it
            // through the file copy, and ReadMetadataAsync verifies against the LIVE key.
            await db.RunAsync(c =>
            {
                if (c.Find<MetaRow>(HmacKeyMeta) is null)
                    c.InsertOrReplace(new MetaRow { Key = HmacKeyMeta, Value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
            }, ct);
            await db.CheckpointAsync(ct);
            await db.RunAsync(_ => File.Copy(db.DatabasePath, path, overwrite), ct);
            // Make the copy self-contained: a WAL-mode file can't always be opened read-only on its own.
            using (var copy = new SQLiteConnection(path))
            {
                copy.ExecuteScalar<string>("PRAGMA journal_mode=DELETE");
                StampBackup(copy);   // SEC-6: created-at + integrity HMAC, written into the copy's Meta
            }
            log.LogInformation("Backup exported ({Bytes} bytes)", new FileInfo(path).Length);
            return Result.Success(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SQLiteException)
        {
            log.LogError(ex, "Backup export failed");
            return Result.Failure<string>(ErrorCodes.BackupFailed, ex.Message);
        }
    }

    // FIX(SEC-6, 2026-10-20, FIX-2): Flaw: a backup carried no provenance — before a restore swap the
    // shopkeeper could not see when it was made or how much data it held, and nothing detected a backup
    // whose contents were edited after export. Fix: every export stamps the copy's Meta with
    // backup.created_ms and backup.hmac (HMAC-SHA256 over a canonical dump of every table, keyed by a
    // device-local key kept in Meta), and ReadMetadataAsync exposes created-at + bill/customer/item
    // counts + HMAC validity for the pre-restore confirmation (the dialog itself is FIX-3's). Restore
    // deliberately does NOT reject on HMAC: a backup restored on a new phone legitimately fails it
    // (different device key) — the signature informs, the shopkeeper decides. Logged in docs/AGENT_FIX_LOG.md.
    private const string HmacKeyMeta = "backup.hmac_key";
    private const string HmacMeta = "backup.hmac";
    private const string CreatedMeta = "backup.created_ms";

    private void StampBackup(SQLiteConnection copy)
    {
        copy.InsertOrReplace(new MetaRow { Key = CreatedMeta, Value = clock.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) });
        var key = Convert.FromBase64String(copy.Find<MetaRow>(HmacKeyMeta)!.Value);
        copy.InsertOrReplace(new MetaRow { Key = HmacMeta, Value = ComputeHmac(copy, key) });
    }

    private static string ComputeHmac(SQLiteConnection c, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(CanonicalDump(c))));
    }

    /// <summary>Deterministic dump of every table (Meta minus the HMAC row itself), rows ordered by
    /// their JSON, so the same logical database always produces the same MAC input.</summary>
    private static string CanonicalDump(SQLiteConnection c)
    {
        var sb = new StringBuilder();
        Append<ItemRow>(c, sb); Append<BillRow>(c, sb); Append<BillLineRow>(c, sb); Append<CustomerRow>(c, sb);
        Append<DuesEntryRow>(c, sb); Append<StockMoveRow>(c, sb); Append<OutboxRow>(c, sb); Append<ShopProfileRow>(c, sb);
        Append<NoteRow>(c, sb);
        sb.Append("MetaRow\n");
        foreach (var row in c.Table<MetaRow>().Where(m => m.Key != HmacMeta).ToList().OrderBy(r => JsonSerializer.Serialize(r)))
            sb.Append(JsonSerializer.Serialize(row)).Append('\n');
        return sb.ToString();

        static void Append<T>(SQLiteConnection conn, StringBuilder b) where T : new()
        {
            b.Append(typeof(T).Name).Append('\n');
            foreach (var row in conn.Table<T>().ToList().OrderBy(r => JsonSerializer.Serialize(r)))
                b.Append(JsonSerializer.Serialize(row)).Append('\n');
        }
    }

    public async Task<BackupMetadata?> ReadMetadataAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            using var c = new SQLiteConnection(filePath, SQLiteOpenFlags.ReadOnly);
            if (c.ExecuteScalar<string>("SELECT Value FROM Meta WHERE Key = 'app'") != AngadiDb.AppMarker) return null;
            var createdRow = c.Find<MetaRow>(CreatedMeta);
            var created = createdRow is not null && long.TryParse(createdRow.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            var storedHmac = c.Find<MetaRow>(HmacMeta)?.Value;
            var valid = false;
            if (storedHmac is not null)
            {
                var deviceKey = await db.RunAsync(cc => cc.Find<MetaRow>(HmacKeyMeta)?.Value, ct);
                if (deviceKey is not null)
                    valid = string.Equals(ComputeHmac(c, Convert.FromBase64String(deviceKey)), storedHmac, StringComparison.Ordinal);
            }
            return new BackupMetadata(created,
                c.ExecuteScalar<int>("SELECT COUNT(*) FROM Bill"),
                c.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer"),
                c.ExecuteScalar<int>("SELECT COUNT(*) FROM Item"),
                storedHmac is not null, valid);
        }
        catch (SQLiteException)
        {
            return null;
        }
    }

    public async Task<Result> RestoreAsync(string filePath, CancellationToken ct = default)
    {
        // H3: never touch the live DB with an unvalidated file. Stage a private copy next to it (same volume,
        // so the final swap is an atomic rename), validate the copy, then swap.
        var staged = db.DatabasePath + ".restore-tmp";
        try
        {
            File.Copy(filePath, staged, overwrite: true);
            if (!IsAngadiBackup(staged, out var reason) || !PassesInvariants(staged, out reason))
            {
                log.LogWarning("Restore rejected: {Reason}", reason);
                return Result.Failure(ErrorCodes.RestoreInvalidFile, reason);
            }

            // A safety copy the shopkeeper can share, besides the ".pre-restore" file the swap keeps.
            // DATA-1b: overwrite: false — the safety copy must never replace (or be replaced by) another export.
            var safety = await ExportCoreAsync(overwrite: false, ct);
            if (safety.IsFailure) return Result.Failure(safety.Error);

            // FIX(RESTORE-1, 2026-10-20, FIX-2): Flaw: the swap replaced NextBillSequence and the quota
            // counters with the backup's older values. Restoring yesterday's backup after today's sales
            // made the next bills REUSE already-printed numbers (UNIQUE violation -> finalize fails, the
            // counter frozen) and reopened spent free quota. Fix: snapshot the live sequence + quota Meta
            // before the swap; after it, write back the element-wise maximum. Books roll back; numbering
            // and quota never do. Why: bill numbers are printed on paper in customers' hands — a duplicate
            // is two different sales claiming to be the same bill. Logged in docs/AGENT_FIX_LOG.md.
            long liveSequence = 1;
            var liveQuota = new Dictionary<string, long>();
            await db.RunAsync(c =>
            {
                liveSequence = c.Table<ShopProfileRow>().FirstOrDefault()?.NextBillSequence ?? 1;
                foreach (var m in c.Table<MetaRow>())
                    if (m.Key.StartsWith("quota.", StringComparison.Ordinal)
                        && long.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        liveQuota[m.Key] = v;
            }, ct);

            await db.ReplaceWithAsync(staged, ct);
            await db.RunAsync(c =>
            {
                var shop = c.Table<ShopProfileRow>().FirstOrDefault();
                if (shop is not null && liveSequence > shop.NextBillSequence)
                {
                    shop.NextBillSequence = liveSequence;
                    c.Update(shop);
                }
                foreach (var (key, value) in liveQuota)
                {
                    var row = c.Find<MetaRow>(key);
                    var current = row is not null
                        && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cv) ? cv : long.MinValue;
                    if (value > current)
                        c.InsertOrReplace(new MetaRow { Key = key, Value = value.ToString(CultureInfo.InvariantCulture) });
                }
                // M-3: the buckets above were just healed to the live maxima, so this DB has absorbed the
                // device's full lifetime — stamp the watermark, or QuotaStore would floor the count a
                // second time from the lifetime counter.
                c.InsertOrReplace(new MetaRow { Key = QuotaStore.WatermarkKey, Value = LifetimeBillCounter.Read(db.DatabasePath).ToString(CultureInfo.InvariantCulture) });
            }, ct);
            log.LogInformation("Restore completed; previous data kept at {Path}", Path.GetFileName(safety.Value));
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SQLiteException)
        {
            log.LogError(ex, "Restore failed; live data unchanged");
            return Result.Failure(ErrorCodes.RestoreInvalidFile, ex.GetType().Name);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    /// <summary>
    /// H3 invariant sweep on the staged copy (read-only, disposed before the swap). Negative stock is LEGAL
    /// ("sold before stock-in recorded") so it is not checked; ledgers must reconcile instead.
    /// </summary>
    internal static bool PassesInvariants(string path, out string reason)
    {
        reason = "";
        using var c = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly);
        var integrity = c.ExecuteScalar<string>("PRAGMA integrity_check");
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)) { reason = "integrity_check: " + integrity; return false; }

        var stockMismatch = c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Item i WHERE i.StockMilli <> (SELECT COALESCE(SUM(m.DeltaMilli), 0) FROM StockMove m WHERE m.ItemId = i.Id)");
        if (stockMismatch > 0) { reason = $"{stockMismatch} item(s) whose stock does not match the stock ledger"; return false; }

        var duesMismatch = c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Customer cu WHERE cu.DuesBalancePaise <> (SELECT COALESCE(SUM(CASE e.Kind WHEN 0 THEN e.AmountPaise ELSE -e.AmountPaise END), 0) FROM DuesEntry e WHERE e.CustomerId = cu.Id)");
        if (duesMismatch > 0) { reason = $"{duesMismatch} customer(s) whose dues do not match the dues ledger"; return false; }

        var negativeDues = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer WHERE DuesBalancePaise < 0");
        if (negativeDues > 0) { reason = $"{negativeDues} customer(s) with negative dues"; return false; }

        var orphanLines = c.ExecuteScalar<int>("SELECT COUNT(*) FROM BillLine l WHERE NOT EXISTS (SELECT 1 FROM Bill b WHERE b.Id = l.BillId)");
        if (orphanLines > 0) { reason = $"{orphanLines} bill line(s) without a bill"; return false; }

        var orphanMoves = c.ExecuteScalar<int>("SELECT COUNT(*) FROM StockMove m WHERE NOT EXISTS (SELECT 1 FROM Item i WHERE i.Id = m.ItemId)");
        if (orphanMoves > 0) { reason = $"{orphanMoves} stock move(s) without an item"; return false; }

        var orphanDues = c.ExecuteScalar<int>("SELECT COUNT(*) FROM DuesEntry e WHERE NOT EXISTS (SELECT 1 FROM Customer cu WHERE cu.Id = e.CustomerId)");
        if (orphanDues > 0) { reason = $"{orphanDues} dues entries without a customer"; return false; }

        // FIX(RESTORE-2, 2026-10-20, FIX-2): Flaw: the sweep reconciled the ledgers but never recomputed
        // a single bill — a backup with hand-edited bill totals restored silently, and a rewound
        // NextBillSequence restored with it (duplicate numbers on the next sales). Fix: recompute every
        // non-draft bill with the same BillCalculator the counter uses and compare every frozen total and
        // tax split; reject any bill number at or beyond NextBillSequence; the orphan ledger checks above
        // are T3-03. Why: a backup is the shop's disaster recovery — whatever it accepts becomes the books
        // of record. Logged in docs/AGENT_FIX_LOG.md.
        var nextSequence = c.Table<ShopProfileRow>().FirstOrDefault()?.NextBillSequence ?? 1;
        foreach (var billRow in c.Table<BillRow>().Where(b => b.Status != 0))
        {
            var lines = c.Table<BillLineRow>().Where(l => l.BillId == billRow.Id).ToList();
            try
            {
                var copy = Mapping.ToDomain(billRow, lines);
                var storedSplits = copy.TaxSplits;
                BillCalculator.Compute(copy, copy.IsTaxInvoice);
                var splitsMatch = copy.TaxSplits.Count == storedSplits.Count && copy.TaxSplits.Zip(storedSplits)
                    .All(p => p.First.Slab == p.Second.Slab && p.First.Taxable.Value == p.Second.Taxable.Value && p.First.Tax.Value == p.Second.Tax.Value);
                if (copy.SubTotal.Value != billRow.SubTotalPaise || copy.BillDiscountAmount.Value != billRow.BillDiscountPaise
                    || copy.TaxTotal.Value != billRow.TaxTotalPaise || copy.RoundingAdjustment.Value != billRow.RoundingPaise
                    || copy.GrandTotal.Value != billRow.GrandTotalPaise || !splitsMatch)
                { reason = $"bill {billRow.Number} totals do not recompute"; return false; }
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException or System.Text.Json.JsonException)
            { reason = $"bill {billRow.Number} cannot be recomputed ({ex.GetType().Name})"; return false; }

            var dash = billRow.Number.LastIndexOf('-');
            if (dash >= 0 && long.TryParse(billRow.Number.AsSpan(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix)
                && suffix >= nextSequence)
            { reason = $"bill {billRow.Number} is at or beyond the next bill number"; return false; }
        }
        return true;
    }

    internal static bool IsAngadiBackup(string path, out string reason)
    {
        reason = "";
        if (!File.Exists(path)) { reason = "file not found"; return false; }
        try
        {
            using var c = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly);
            var app = c.ExecuteScalar<string>("SELECT Value FROM Meta WHERE Key = 'app'");
            var version = c.ExecuteScalar<string>("SELECT Value FROM Meta WHERE Key = 'schema_version'");
            if (app != AngadiDb.AppMarker) { reason = "not an Angadi database"; return false; }
            if (!int.TryParse(version, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) || v > AngadiDb.SchemaVersion) { reason = $"schema {version} is newer than this app"; return false; }
            return true;
        }
        catch (SQLiteException ex)
        {
            reason = "not a readable database: " + ex.Message;
            return false;
        }
    }
}
