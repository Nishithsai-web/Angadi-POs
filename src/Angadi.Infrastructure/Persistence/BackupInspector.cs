using Angadi.Application.Platform;
using SQLite;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// FIX(SEC-6, 2026-10-20, FIX-3): Flaw: restore proved a backup was internally consistent, not that it was
/// CURRENT — restoring a months-old file silently rolled the books back behind a generic warning.
/// Fix: before the swap, the restore screen inspects the staged file (read-only, its own connection —
/// the live database is never opened) and shows the backup's bill/customer/item counts and newest bill
/// date, so the owner confirms with the facts on screen. Restore itself is unchanged (BackupService
/// re-validates everything). Why: an informed tap is the difference between a recovery and a second
/// disaster. Logged in docs/AGENT_FIX_LOG.md.
/// </summary>
internal sealed class BackupInspector : IBackupInspector
{
    public BackupInfo? Inspect(string filePath)
    {
        if (!BackupService.IsAngadiBackup(filePath, out _)) return null;
        try
        {
            using var c = new SQLiteConnection(filePath, SQLiteOpenFlags.ReadOnly);
            var bills = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Bill");
            var customers = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer");
            var items = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Item");
            var newestMs = c.ExecuteScalar<long?>("SELECT MAX(FinalizedAtMs) FROM Bill");
            return new BackupInfo(
                newestMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
                bills, customers, items);
        }
        catch (SQLiteException)
        {
            return null;   // unreadable as a database: the restore screen treats it as an invalid file
        }
    }
}
