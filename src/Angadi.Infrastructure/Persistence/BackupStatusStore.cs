using System.Globalization;
using Angadi.Application.Catalog;

namespace Angadi.Infrastructure.Persistence;

/// <summary>A2-03: manual-backup bookkeeping in Meta rows ("backup.*"); the size is the database file plus its WAL.</summary>
internal sealed class BackupStatusStore(AngadiDb db) : IBackupStatusStore
{
    internal const string LastKey = "backup.last_manual";
    internal const string FirstSeenKey = "backup.first_seen";
    internal const string DismissedKey = "backup.reminder_dismissed";

    public async Task<DateTimeOffset?> LastManualBackupAsync(CancellationToken ct = default)
        => ParseMs(await ReadAsync(LastKey, ct));

    public Task RecordManualBackupAsync(DateTimeOffset at, CancellationToken ct = default)
        => WriteAsync(LastKey, Ms(at), ct);

    public Task<DateTimeOffset> FirstSeenAsync(DateTimeOffset now, CancellationToken ct = default)
        => db.RunAsync(c =>
        {
            if (ParseMs(c.Find<MetaRow>(FirstSeenKey)?.Value) is { } seen) return seen;
            c.InsertOrReplace(new MetaRow { Key = FirstSeenKey, Value = Ms(now) });
            return now;
        }, ct);

    public async Task<DateOnly?> ReminderDismissedOnAsync(CancellationToken ct = default)
        => DateOnly.TryParseExact(await ReadAsync(DismissedKey, ct), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public Task DismissReminderAsync(DateOnly day, CancellationToken ct = default)
        => WriteAsync(DismissedKey, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);

    public long DatabaseBytes()
    {
        long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        try { return Size(db.DatabasePath) + Size(db.DatabasePath + "-wal"); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private Task<string?> ReadAsync(string key, CancellationToken ct) => db.RunAsync(c => c.Find<MetaRow>(key)?.Value, ct);

    private Task WriteAsync(string key, string value, CancellationToken ct)
        => db.RunAsync(c => c.InsertOrReplace(new MetaRow { Key = key, Value = value }), ct);

    private static string Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseMs(string? raw)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}
