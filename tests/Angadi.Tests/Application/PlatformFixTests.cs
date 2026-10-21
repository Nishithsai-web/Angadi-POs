using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Application.Voice;
using Angadi.Domain.Billing;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>Agent F2: backups (A2-03), offline Pro (A2-11), and the exceptions audit (A1-02/05/10/11/12).</summary>
public class PlatformFixTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 0, 0, TimeSpan.FromHours(5.5));
    private static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(t.DateTime);

    // ---------- A2-03 backup reminder rules ----------

    [Fact]
    public void A2_03_NeverBackedUp_RemindsSevenDaysAfterFirstSeen()
    {
        Assert.False(BackupReminder.ShouldRemind(T0.AddDays(6), Day(T0.AddDays(6)), null, T0, null));
        Assert.True(BackupReminder.ShouldRemind(T0.AddDays(7), Day(T0.AddDays(7)), null, T0, null));
    }

    [Fact]
    public void A2_03_RecentBackup_NoReminder_OldBackup_Reminder()
    {
        var firstSeen = T0.AddDays(-100);
        Assert.False(BackupReminder.ShouldRemind(T0, Day(T0), T0.AddDays(-3), firstSeen, null));
        Assert.True(BackupReminder.ShouldRemind(T0, Day(T0), T0.AddDays(-8), firstSeen, null));
    }

    [Fact]
    public void A2_03_DismissedToday_Hidden_ComesBackTomorrow()
    {
        var old = T0.AddDays(-30);
        Assert.False(BackupReminder.ShouldRemind(T0, Day(T0), old, old, Day(T0)));
        Assert.True(BackupReminder.ShouldRemind(T0.AddDays(1), Day(T0.AddDays(1)), old, old, Day(T0)));
    }

    [Fact]
    public void A2_03_ClockMovedBack_StillReminds()
    {
        // A "last backup" in the future (phone clock set back) must not silence the reminder for months.
        Assert.True(BackupReminder.ShouldRemind(T0, Day(T0), T0.AddDays(40), T0.AddDays(-100), null));
    }

    // ---------- A2-03 through the real handlers ----------

    [Fact]
    public async Task A2_03_ManualBackup_IsRecorded_AndClearsTheReminder()
    {
        using var h = new AppHost();
        h.Clock.Now = T0;
        await h.SeedShopAsync();

        var first = await h.Sender.Send(new GetBackupStatusQuery());   // first look: starts the 7-day count
        Assert.Null(first.LastManualBackup);
        Assert.False(first.ShowReminder);
        Assert.True(first.DatabaseBytes > 0);
        Assert.False(first.IsLarge);

        h.Clock.Now = T0.AddDays(8);
        Assert.True((await h.Sender.Send(new GetBackupStatusQuery())).ShowReminder);

        var export = await h.Sender.Send(new ExportBackupCommand());
        Assert.True(export.IsSuccess, export.Error.Code);
        var after = await h.Sender.Send(new GetBackupStatusQuery());
        Assert.Equal(h.Clock.Now.ToUnixTimeMilliseconds(), after.LastManualBackup!.Value.ToUnixTimeMilliseconds());
        Assert.False(after.ShowReminder);

        h.Clock.Now = T0.AddDays(16);
        Assert.True((await h.Sender.Send(new GetBackupStatusQuery())).ShowReminder);
    }

    [Fact]
    public async Task A2_03_Dismiss_HidesForTheRestOfTheDayOnly()
    {
        using var h = new AppHost();
        h.Clock.Now = T0;
        await h.SeedShopAsync();
        await h.Sender.Send(new GetBackupStatusQuery());
        h.Clock.Now = T0.AddDays(10);
        Assert.True((await h.Sender.Send(new GetBackupStatusQuery())).ShowReminder);

        Assert.True((await h.Sender.Send(new DismissBackupReminderCommand())).IsSuccess);
        Assert.False((await h.Sender.Send(new GetBackupStatusQuery())).ShowReminder);

        h.Clock.Now = T0.AddDays(11);
        Assert.True((await h.Sender.Send(new GetBackupStatusQuery())).ShowReminder);
    }

    [Fact]
    public async Task A2_03_FailedExport_IsNotRecordedAsABackup()
    {
        using var h = new AppHost(configure: s => s.AddSingleton<IBackupService>(new FailingBackup()));
        h.Clock.Now = T0;
        var r = await h.Sender.Send(new ExportBackupCommand());
        Assert.Equal(ErrorCodes.BackupFailed, r.Error.Code);
        Assert.Null((await h.Sender.Send(new GetBackupStatusQuery())).LastManualBackup);
    }

    private sealed class FailingBackup : IBackupService
    {
        public Task<Result<string>> ExportAsync(CancellationToken ct = default) => Task.FromResult(Result.Failure<string>(ErrorCodes.BackupFailed, "disk"));
        public Task<Result> RestoreAsync(string filePath, CancellationToken ct = default) => Task.FromResult(Result.Failure(ErrorCodes.RestoreInvalidFile));
        public Task<BackupMetadata?> ReadMetadataAsync(string filePath, CancellationToken ct = default) => Task.FromResult<BackupMetadata?>(null);
        public Task<Result<string>> ExportEncryptedAsync(string passphrase, CancellationToken ct = default) => Task.FromResult(Result.Failure<string>(ErrorCodes.BackupFailed, "disk"));
        public Task<Result> RestoreEncryptedAsync(string filePath, string passphrase, CancellationToken ct = default) => Task.FromResult(Result.Failure(ErrorCodes.RestoreInvalidFile));
    }

    // ---------- A2-11 offline Pro ----------

    [Fact]
    public void A2_11_NoCache_IsFree()
    {
        var s = SubscriptionCachePolicy.Evaluate(null, null, T0);
        Assert.False(s.IsPro);
        Assert.False(s.NeedsReconfirm);
    }

    [Fact]
    public void A2_11_ConfirmedRecently_IsPro()
    {
        var s = SubscriptionCachePolicy.Evaluate(SubscriptionPlan.Monthly, T0.AddDays(-3), T0);
        Assert.True(s.IsPro);
        Assert.False(s.NeedsReconfirm);
    }

    [Fact]
    public void A2_11_ProOfflinePastGrace_StaysPro_AndAsksToConnect()
    {
        var s = SubscriptionCachePolicy.Evaluate(SubscriptionPlan.Yearly, T0.AddDays(-12), T0);
        Assert.True(s.IsPro);            // never blocked at the counter
        Assert.True(s.NeedsReconfirm);   // banner: connect to confirm
    }

    [Fact]
    public async Task A2_11_StaleProShop_KeepsBillingPastTheFreeLimit()
    {
        using var h = new AppHost(configure: s => s.AddSingleton<ISubscriptionService>(new StalePro()));
        await h.SeedShopAsync();
        var item = await h.AddItemAsync("అగ్గిపెట్టె", 200, GstSlab.Five, stockMilli: 1_000_000, unit: Unit.Piece);
        for (var i = 0; i < 51; i++)
        {
            var r = await h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))));
            Assert.True(r.IsSuccess, r.Error.Code);
        }
        var quota = await h.Sender.Send(new GetBillQuotaQuery());
        Assert.True(quota.IsPro);
        Assert.True(quota.NeedsReconfirm);
    }

    private sealed class StalePro : ISubscriptionService
    {
        public Task<SubscriptionStatus> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(SubscriptionCachePolicy.Evaluate(SubscriptionPlan.Monthly, DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now));
        public Task<Result<SubscriptionStatus>> PurchaseAsync(SubscriptionPlan plan, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<SubscriptionStatus>> RestoreAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public void A2_12_PendingPurchase_HasItsOwnMessage()
    {
        var text = new JsonLocalizer();
        foreach (var lang in new[] { Angadi.Domain.Shop.UiLanguage.Telugu, Angadi.Domain.Shop.UiLanguage.English })
        {
            text.Language = lang;
            Assert.NotEqual(text.Error(new Error(ErrorCodes.PurchaseFailed)), text.Error(new Error(ErrorCodes.PurchasePending)));
        }
    }

    // ---------- A1-02 restore swap never leaves the lock held ----------

    [Fact]
    public async Task A1_02_ReopenFailureAfterSwap_ReleasesTheDatabaseLock()
    {
        using var t = new TempDb();
        var junk = Path.Combine(t.Folder, "junk.db");
        await File.WriteAllBytesAsync(junk, Enumerable.Repeat((byte)0x42, 8192).ToArray());

        // Since F6 (FIX-2) a damaged swapped-in file is recovered from ".pre-restore" instead of throwing; either
        // outcome is fine here — what matters is that the gate is released afterwards.
        _ = await Record.ExceptionAsync(() => t.Db.ReplaceWithAsync(junk));

        // Before the fix the gate stayed taken: this waited the full 30 s lock timeout, then DATABASE_BUSY forever.
        var next = await Record.ExceptionAsync(() => t.Db.RunAsync(c => 1).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsNotType<DatabaseBusyException>(next);
        Assert.IsNotType<TimeoutException>(next);
    }

    // ---------- A1-05 database work leaves the caller's thread ----------

    [Fact]
    public async Task A1_05_DatabaseWork_RunsOffTheCallingThread()
    {
        using var t = new TempDb();
        var caller = Environment.CurrentManagedThreadId;
        int? ran = null;
        // A single-threaded context stands in for the UI thread: work must not run inline on it.
        await SingleThread.RunAsync(async () =>
        {
            caller = Environment.CurrentManagedThreadId;
            await t.Db.RunAsync(_ => { ran = Environment.CurrentManagedThreadId; return 0; });
        });
        Assert.NotNull(ran);
        Assert.NotEqual(caller, ran);
    }

    // ---------- A1-10 voice numbers never overflow ----------

    [Theory]
    [InlineData("12345678901234567890")]
    [InlineData("99999999999999999999999999999999")]
    [InlineData("12345678901234567890/3")]
    public void A1_10_HugeNumberTokens_AreNotNumbers(string token)
    {
        Assert.False(new TeluguParser().TryNumber(token, out _));
    }

    [Fact]
    public void A1_10_HugeNumberInAnUtterance_DoesNotThrow()
    {
        var ex = Record.Exception(() => new TeluguParser().Parse("బియ్యం 99999999999999999999999999999999 కేజీలు"));
        Assert.Null(ex);
    }

    // ---------- A1-11 alias learning goes through the pipeline ----------

    [Fact]
    public async Task A1_11_LearnAliasCommand_LearnsThroughTheDispatcher()
    {
        using var h = new AppHost();
        var item = await h.AddItemAsync("బియ్యం", 6000);
        var r = await h.Sender.Send(new LearnAliasCommand(item.Id, "రైస్ సోనా"));
        Assert.True(r.IsSuccess);
        var saved = await h.Sender.Send(new GetItemQuery(item.Id));
        Assert.NotEmpty(saved!.LearnedAliases);
    }

    // ---------- A1-12 overlapping safety snapshots ----------

    [Fact]
    public async Task A1_12_OverlappingSnapshots_AllSucceed()
    {
        using var h = new AppHost();
        await h.SeedShopAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => h.Sender.Send(new TakeSafetySnapshotCommand()))));
        Assert.All(results, r => Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code + " " + r.Error.Detail : ""));
    }

    /// <summary>Runs async work on one dedicated thread with a SynchronizationContext (like the UI thread).</summary>
    private sealed class SingleThread : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public static Task RunAsync(Func<Task> work)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var ctx = new SingleThread();
                SetSynchronizationContext(ctx);
                Task task;
                try { task = work(); }
                catch (Exception ex) { done.SetException(ex); return; }
                task.ContinueWith(_ => ctx._queue.CompleteAdding(), TaskScheduler.Default);
                foreach (var (d, s) in ctx._queue.GetConsumingEnumerable()) d(s);
                if (task.IsFaulted) done.SetException(task.Exception!.InnerExceptions);
                else done.SetResult();
            });
            thread.Start();
            return done.Task;
        }
    }
}
