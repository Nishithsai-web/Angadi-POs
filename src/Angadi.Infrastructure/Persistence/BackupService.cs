using Angadi.Application.Common;
using Angadi.Application.Platform;
using Microsoft.Extensions.Logging;
using SQLite;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// FR-37. A backup is a copy of the SQLite file after a WAL checkpoint. Restore checks that the file
/// is an Angadi database with a schema version this build understands before swapping it in.
/// </summary>
internal sealed class BackupService(AngadiDb db, IClock clock, string exportDirectory, ILogger<BackupService> log) : IBackupService
{
    public async Task<Result<string>> ExportAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(exportDirectory);
            var path = Path.Combine(exportDirectory, $"angadi-backup-{clock.Now:yyyyMMdd-HHmm}.db");
            await db.CheckpointAsync(ct);
            await db.RunAsync(_ => File.Copy(db.DatabasePath, path, overwrite: true), ct);
            // Make the copy self-contained: a WAL-mode file can't always be opened read-only on its own.
            using (var copy = new SQLiteConnection(path)) copy.ExecuteScalar<string>("PRAGMA journal_mode=DELETE");
            log.LogInformation("Backup exported ({Bytes} bytes)", new FileInfo(path).Length);
            return Result.Success(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SQLiteException)
        {
            log.LogError(ex, "Backup export failed");
            return Result.Failure<string>(ErrorCodes.BackupFailed, ex.Message);
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
            var safety = await ExportAsync(ct);
            if (safety.IsFailure) return Result.Failure(safety.Error);

            await db.ReplaceWithAsync(staged, ct);
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
