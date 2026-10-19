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
        if (!IsAngadiBackup(filePath, out var reason))
        {
            log.LogWarning("Restore rejected: {Reason}", reason);
            return Result.Failure(ErrorCodes.RestoreInvalidFile, reason);
        }

        // Keep a safety copy of the current data first; a bad restore must be undoable.
        var safety = await ExportAsync(ct);
        if (safety.IsFailure) return Result.Failure(safety.Error);

        await db.ReplaceWithAsync(filePath, ct);
        log.LogInformation("Restore completed; previous data kept at {Path}", Path.GetFileName(safety.Value));
        return Result.Success();
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
            if (!int.TryParse(version, out var v) || v > AngadiDb.SchemaVersion) { reason = $"schema {version} is newer than this app"; return false; }
            return true;
        }
        catch (SQLiteException ex)
        {
            reason = "not a readable database: " + ex.Message;
            return false;
        }
    }
}
