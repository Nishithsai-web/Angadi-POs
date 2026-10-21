using Angadi.Application.Common;
using Angadi.Application.Platform;

namespace Angadi.Application.Catalog;

/// <summary>Settings line + counter reminder: how big the data is and when the owner last took a manual backup.</summary>
public sealed record GetBackupStatusQuery : IQuery<BackupStatus>;

/// <summary>"✕" on the counter's backup reminder: hide it for the rest of today.</summary>
public sealed record DismissBackupReminderCommand : ICommand<Result>, INoTransaction;

/// <param name="LastManualBackup">Last successful "Back up data" (null = never).</param>
/// <param name="DatabaseBytes">The database file plus its write-ahead log.</param>
/// <param name="ShowReminder">Counter banner: no manual backup for 7 days, and not dismissed today.</param>
/// <param name="IsLarge">Past ~20 MB: Android's automatic backup (25 MB cap, Wi-Fi only) can no longer be relied on.</param>
public sealed record BackupStatus(DateTimeOffset? LastManualBackup, long DatabaseBytes, bool ShowReminder, bool IsLarge);

/// <summary>Meta-backed bookkeeping for manual backups (keys "backup.*").</summary>
public interface IBackupStatusStore
{
    Task<DateTimeOffset?> LastManualBackupAsync(CancellationToken ct = default);
    Task RecordManualBackupAsync(DateTimeOffset at, CancellationToken ct = default);
    /// <summary>When this install first asked (stored on the first call): "never backed up" counts from here.</summary>
    Task<DateTimeOffset> FirstSeenAsync(DateTimeOffset now, CancellationToken ct = default);
    Task<DateOnly?> ReminderDismissedOnAsync(CancellationToken ct = default);
    Task DismissReminderAsync(DateOnly day, CancellationToken ct = default);
    long DatabaseBytes();
}

/// <summary>
/// A2-03. Android Auto Backup stops past 25 MB (~7,000 bills) and only runs on Wi-Fi, so the owner's manual backup
/// (shared to WhatsApp/Drive) is the real safety net. Pure rules, unit-tested.
/// </summary>
public static class BackupReminder
{
    public static readonly TimeSpan RemindAfter = TimeSpan.FromDays(7);
    public const long LargeBytes = 20L * 1024 * 1024;

    public static bool ShouldRemind(DateTimeOffset now, DateOnly today, DateTimeOffset? lastBackup, DateTimeOffset firstSeen, DateOnly? dismissedOn)
    {
        if (dismissedOn == today) return false;
        var since = lastBackup ?? firstSeen;
        // A clock moved back (since in the future) must not hide the reminder forever: treat it as due.
        return since > now || now - since >= RemindAfter;
    }
}

internal sealed class BackupHandlers(IBackupService backup, IBackupStatusStore status, IClock clock) :
    IRequestHandler<ExportBackupCommand, Result<string>>,
    IRequestHandler<GetBackupStatusQuery, BackupStatus>,
    IRequestHandler<DismissBackupReminderCommand, Result>
{
    public async Task<Result<string>> Handle(ExportBackupCommand command, CancellationToken ct)
    {
        var r = await backup.ExportAsync(ct);
        if (r.IsSuccess) await status.RecordManualBackupAsync(clock.Now, ct);
        return r;
    }

    public async Task<BackupStatus> Handle(GetBackupStatusQuery query, CancellationToken ct)
    {
        var now = clock.Now;
        var last = await status.LastManualBackupAsync(ct);
        var firstSeen = await status.FirstSeenAsync(now, ct);
        var dismissed = await status.ReminderDismissedOnAsync(ct);
        var bytes = status.DatabaseBytes();
        return new BackupStatus(last, bytes, BackupReminder.ShouldRemind(now, clock.Today, last, firstSeen, dismissed), bytes >= BackupReminder.LargeBytes);
    }

    public async Task<Result> Handle(DismissBackupReminderCommand command, CancellationToken ct)
    {
        await status.DismissReminderAsync(clock.Today, ct);
        return Result.Success();
    }
}
