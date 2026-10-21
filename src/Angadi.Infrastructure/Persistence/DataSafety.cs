using System.Globalization;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Microsoft.Extensions.Logging;
using SQLite;

namespace Angadi.Infrastructure.Persistence;

/// <summary>The live database failed SQLite's own checks at startup (corrupt pages, not a database file).</summary>
public sealed class DamagedDatabaseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Startup recovery. Flaw it fixes: a damaged database file (power cut mid-write on a cheap phone, a bad SD card)
/// made the app fail at launch with no way back short of reinstalling. Now the damaged file is moved aside — never
/// deleted, support may need it — and the newest copy that passes every check is put in its place.
/// </summary>
internal static class DatabaseRecovery
{
    internal const string SnapshotPattern = "angadi-*.db";
    private const int KeepDamaged = 2;

    public static DataRecoveryReport Recover(string path, string snapshotDirectory, string reason, DateTimeOffset now)
    {
        var damaged = path + ".damaged-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        MoveAside(path, damaged);
        MoveAside(path + "-wal", damaged + "-wal");
        DeleteQuietly(path + "-shm");
        PruneDamaged(path);

        // FIX(LOG-3e, 2026-10-20, FIX-3): Flaw: a candidate that failed to copy was skipped with NO record —
        // support could not tell a skipped-because-corrupt copy from one never tried. Fix: every skip reason
        // is collected into the recovery report's Reason, which DataSafety.TakeRecoveryReport logs as a
        // warning. Why: recovery is the worst day in the shop's life with this app; it must explain itself.
        // Logged in docs/AGENT_FIX_LOG.md.
        var skipped = new List<string>();
        foreach (var candidate in Candidates(path, snapshotDirectory))
        {
            try
            {
                File.Copy(candidate, path, overwrite: true);
                if (IsGoodDatabase(path, out _))
                    return new DataRecoveryReport(now, new DateTimeOffset(File.GetLastWriteTimeUtc(candidate), TimeSpan.Zero), WithSkips(reason, skipped));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(Path.GetFileName(candidate) + ": " + ex.GetType().Name);
            }
            DeleteQuietly(path);
            DeleteQuietly(path + "-wal");
            DeleteQuietly(path + "-shm");
        }
        return new DataRecoveryReport(now, null, WithSkips(reason, skipped));   // no good copy: start empty; Settings → Restore still works
    }

    private static string WithSkips(string reason, List<string> skipped) =>
        skipped.Count == 0 ? reason : reason + "; skipped: " + string.Join(", ", skipped);

    /// <summary>Newest safety copy first, then the copies kept by an app update and by the last restore.</summary>
    private static IEnumerable<string> Candidates(string path, string snapshotDirectory)
    {
        if (Directory.Exists(snapshotDirectory))
            foreach (var f in Directory.GetFiles(snapshotDirectory, SnapshotPattern).OrderByDescending(x => File.GetLastWriteTimeUtc(x)))
                yield return f;
        foreach (var f in new[] { path + ".pre-update", path + ".pre-restore" })
            if (File.Exists(f)) yield return f;
    }

    /// <summary>
    /// An Angadi database this build understands, passing quick_check. Opened read-write so a WAL-mode copy opens on
    /// its own; switching it to rollback-journal mode makes the file self-contained.
    /// </summary>
    public static bool IsGoodDatabase(string path, out string reason)
    {
        reason = "";
        try
        {
            using var c = new SQLiteConnection(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex);
            c.ExecuteScalar<string>("PRAGMA journal_mode=DELETE");
            var check = c.ExecuteScalar<string>("PRAGMA quick_check");
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase)) { reason = "quick_check: " + check; return false; }
            var app = c.ExecuteScalar<string>("SELECT Value FROM Meta WHERE Key = 'app'");
            if (app != AngadiDb.AppMarker) { reason = "not an Angadi database"; return false; }
            var version = c.ExecuteScalar<string>("SELECT Value FROM Meta WHERE Key = 'schema_version'");
            if (!int.TryParse(version, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v > AngadiDb.SchemaVersion)
            {
                reason = "schema " + version + " is newer than this app";
                return false;
            }
            return true;
        }
        catch (SQLiteException ex)
        {
            reason = ex.Result + ": " + ex.Message;
            return false;
        }
    }

    private static void MoveAside(string from, string to)
    {
        if (File.Exists(from)) File.Move(from, to, overwrite: true);
    }

    private static void PruneDamaged(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var old = Directory.GetFiles(dir, Path.GetFileName(path) + ".damaged-*")
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal))
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Skip(KeepDamaged);
        foreach (var f in old)
        {
            DeleteQuietly(f);
            DeleteQuietly(f + "-wal");
        }
    }

    internal static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Rolling safety copies: one file per day ("angadi-YYYYMMDD.db"), refreshed whenever the data changed, the newest
/// <see cref="KeepDays"/> kept. Each copy is validated before it replaces the previous one, so a bad copy can never
/// overwrite a good one.
/// </summary>
internal sealed class DataSafety(AngadiDb db, IClock clock, ILogger<DataSafety> log) : IDataSafety
{
    public const int KeepDays = 3;
    private const long HeadroomBytes = 20L * 1024 * 1024;
    private long _lastChanges = -1;
    private string? _lastTarget;

    public DataRecoveryReport? TakeRecoveryReport()
    {
        var report = db.TakeRecovery();
        if (report is not null)
            log.LogWarning("Startup recovery: damaged database moved aside ({Reason}); restored copy from {RestoredFrom}",
                report.Reason, report.RestoredFrom?.ToString("u", CultureInfo.InvariantCulture) ?? "none");
        return report;
    }

    /// <summary>
    /// A1-12: OnStart and OnSleep can overlap (quick app switches); both used the same ".tmp" path, so one run could
    /// validate or move the other's half-written copy. One snapshot at a time.
    /// </summary>
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);

    public async Task<Result> SnapshotAsync(CancellationToken ct = default)
    {
        await _snapshotGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await SnapshotCoreAsync(ct).ConfigureAwait(false); }
        finally { _snapshotGate.Release(); }
    }

    private async Task<Result> SnapshotCoreAsync(CancellationToken ct)
    {
        var dir = db.SnapshotDirectory;
        var target = Path.Combine(dir, "angadi-" + clock.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".db");
        var temp = target + ".tmp";
        try
        {
            Directory.CreateDirectory(dir);
            // Inside the lock: nothing else writes while the file is copied, so the copy is consistent.
            var copied = await db.RunAsync(c =>
            {
                var changes = c.ExecuteScalar<long>("SELECT total_changes()");
                if (changes == _lastChanges && target == _lastTarget && File.Exists(target)) return false;
                if (!HasRoom(db.DatabasePath, dir)) throw new StorageFullException("no room for a safety copy");
                c.ExecuteScalar<int>("PRAGMA wal_checkpoint(TRUNCATE)");
                File.Copy(db.DatabasePath, temp, overwrite: true);
                _lastChanges = changes;
                _lastTarget = target;
                return true;
            }, ct);
            if (!copied) return Result.Success();

            if (!DatabaseRecovery.IsGoodDatabase(temp, out var why))
            {
                _lastChanges = -1;
                DatabaseRecovery.DeleteQuietly(temp);
                log.LogError("Safety copy failed its checks and was discarded: {Reason}", why);
                return Result.Failure(ErrorCodes.BackupFailed, why);
            }
            DatabaseRecovery.DeleteQuietly(temp + "-wal");
            DatabaseRecovery.DeleteQuietly(temp + "-shm");
            File.Move(temp, target, overwrite: true);
            Prune(dir);
            log.LogInformation("Safety copy saved ({Bytes} bytes)", new FileInfo(target).Length);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SQLiteException)
        {
            _lastChanges = -1;
            DatabaseRecovery.DeleteQuietly(temp);
            log.LogWarning(ex, "Safety copy skipped");
            return Result.Failure(StorageFailures.Classify(ex) ?? ErrorCodes.BackupFailed, ex.GetType().Name);
        }
    }

    private static bool HasRoom(string databasePath, string dir)
    {
        try
        {
            var size = new FileInfo(databasePath).Length;
            return new DriveInfo(dir).AvailableFreeSpace > size * 2 + HeadroomBytes;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return true;   // can't tell: try, and let a real full disk fail the copy
        }
    }

    private static void Prune(string dir)
    {
        foreach (var old in Directory.GetFiles(dir, DatabaseRecovery.SnapshotPattern)
                     .OrderByDescending(f => f, StringComparer.Ordinal).Skip(KeepDays))
            DatabaseRecovery.DeleteQuietly(old);
    }
}
