using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using SQLite;

namespace Angadi.Tests.Application;

/// <summary>H2/B2 quota clock defence and H3 safe restore.</summary>
public class QuotaAndRestoreTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static async Task<Item> ReadyAsync(AppHost h)
    {
        await h.SeedShopAsync();
        return await h.AddItemAsync("అగ్గిపెట్టె", 200, GstSlab.Five, stockMilli: 1_000_000, unit: Unit.Piece);
    }

    private static Task<Result<FinalizedBill>> SellAsync(AppHost h, Item item) =>
        h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))));

    [Fact]
    public async Task H2_ClockRolledBack_DoesNotReopenSpentMonth()
    {
        using var h = new AppHost();
        h.Clock.Now = new DateTimeOffset(2026, 11, 1, 0, 30, 0, Ist);
        var item = await ReadyAsync(h);
        for (var i = 0; i < 50; i++) Assert.True((await SellAsync(h, item)).IsSuccess);

        // Back across the month boundary, inside QUOTA-1's 48 h sanity bound: the spent month stays pinned.
        h.Clock.Now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, Ist);
        Assert.Equal(ErrorCodes.FreeLimitReached, (await SellAsync(h, item)).Error.Code);
        Assert.Equal(50, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    [Fact]
    public async Task H2_ClockRolledForward_SpendsOnlyTheFutureBucket()
    {
        using var h = new AppHost();
        h.Clock.Now = new DateTimeOffset(2026, 11, 10, 10, 0, 0, Ist);
        var item = await ReadyAsync(h);
        Assert.True((await SellAsync(h, item)).IsSuccess);                       // Nov: 1

        h.Clock.Now = new DateTimeOffset(2027, 1, 5, 10, 0, 0, Ist);              // forward to Jan
        Assert.True((await SellAsync(h, item)).IsSuccess);                       // Jan: 1

        h.Clock.Now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, Ist);            // back to Nov
        var quota = await h.Sender.Send(new GetBillQuotaQuery());
        // QUOTA-1: last_seen (Jan) is >48 h ahead of now, so it no longer pins the read; November's own
        // bucket governs — and it holds November's one real bill, so the count is still 1, not a fresh 0.
        Assert.Equal(1, quota.UsedThisMonth);
    }

    [Fact]
    public async Task QUOTA1_ClockJumpedAYearAhead_ThenCorrected_DoesNotPoisonTheQuota()
    {
        using var h = new AppHost();
        h.Clock.Now = new DateTimeOffset(2026, 11, 10, 10, 0, 0, Ist);
        var item = await ReadyAsync(h);
        Assert.True((await SellAsync(h, item)).IsSuccess);                        // real November: 1

        h.Clock.Now = new DateTimeOffset(2027, 11, 10, 10, 0, 0, Ist);            // the accident: +1 year
        Assert.True((await SellAsync(h, item)).IsSuccess);                        // one bill in the wrong year

        h.Clock.Now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, Ist);            // clock corrected
        Assert.Equal(1, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);   // real November governs
        Assert.True((await SellAsync(h, item)).IsSuccess);
        Assert.Equal(2, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    [Fact]
    public async Task T3_11_QuotaCountRowMissing_SeedsFromBills()
    {
        using var h = new AppHost();
        var item = await ReadyAsync(h);
        for (var i = 0; i < 3; i++) Assert.True((await SellAsync(h, item)).IsSuccess);

        // The counter row vanishes (older build, partial restore, hand-repaired DB): the count must be
        // rebuilt from the bills themselves, never silently reset to zero.
        await h.Services.GetRequiredService<AngadiDb>().RunAsync(c => c.Execute("DELETE FROM Meta WHERE Key LIKE 'quota.count.%'"));
        Assert.Equal(3, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    [Fact]
    public async Task B2_QuotaCountsEachFinalizeExactlyOnce()
    {
        using var h = new AppHost();
        var item = await ReadyAsync(h);
        for (var i = 0; i < 3; i++) await SellAsync(h, item);
        Assert.Equal(3, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    [Fact]
    public async Task H3_TamperedBackup_IsRejected_AndLiveDataIsUntouched()
    {
        using var t = new TempDb();
        var rice = new Item { TeluguName = "బియ్యం", EnglishName = "Rice", Unit = Unit.Kilogram, Price = new Paise(6200) };
        await t.Items.SaveAsync(rice);
        await t.Ledger.AdjustAsync(rice.Id, Qty.Whole(10), StockMoveReason.Purchase);
        var backup = new BackupService(t.Db, t.Clock, Path.Combine(t.Folder, "exports"), TempDb.Log<BackupService>());

        // Craft a backup whose stock was edited outside the ledger (e.g. hand-made to inflate stock).
        await t.Db.RunAsync(c => c.Execute("UPDATE Item SET StockMilli = 999000 WHERE Id = ?", rice.Id));
        var tampered = (await backup.ExportAsync()).Value;
        await t.Db.RunAsync(c => c.Execute("UPDATE Item SET StockMilli = 10000 WHERE Id = ?", rice.Id));

        t.Clock.Advance(TimeSpan.FromMinutes(5));
        var result = await backup.RestoreAsync(tampered);

        Assert.Equal(ErrorCodes.RestoreInvalidFile, result.Error.Code);
        Assert.Contains("stock", result.Error.Detail);
        Assert.Equal(10_000, (await t.Items.GetAsync(rice.Id))!.Stock.Milli);
    }

    [Fact]
    public async Task H3_NegativeStockIsLegal_AndRestores()
    {
        using var t = new TempDb();
        var rice = new Item { TeluguName = "బియ్యం", EnglishName = "Rice", Unit = Unit.Kilogram, Price = new Paise(6200) };
        await t.Items.SaveAsync(rice);
        await t.Ledger.AdjustAsync(rice.Id, new Qty(-2000), StockMoveReason.CountCorrection);   // sold before stock-in
        var backup = new BackupService(t.Db, t.Clock, Path.Combine(t.Folder, "exports"), TempDb.Log<BackupService>());
        var snapshot = (await backup.ExportAsync()).Value;

        t.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await backup.RestoreAsync(snapshot)).IsSuccess);
        Assert.Equal(-2000, (await t.Items.GetAsync(rice.Id))!.Stock.Milli);
    }

    [Fact]
    public async Task Restore_NewerBillsExist_SequenceAndQuotaDoNotRewind()
    {
        using var h = new AppHost();
        var item = await ReadyAsync(h);
        Assert.Equal("A-000001", (await SellAsync(h, item)).Value.Number);

        var backupPath = (await h.Sender.Send(new ExportBackupCommand())).Value;   // snapshot at 1 bill

        for (var i = 0; i < 5; i++) Assert.True((await SellAsync(h, item)).IsSuccess);   // 6 bills total
        Assert.Equal(6, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);

        Assert.True((await h.Sender.Send(new RestoreBackupCommand(backupPath))).IsSuccess);

        // The books rolled back to the 1-bill snapshot — numbering and quota must not: the next bill is
        // A-000007 (never a duplicate of an already-printed number) and the month count includes all 7.
        Assert.Equal("A-000007", (await SellAsync(h, item)).Value.Number);
        Assert.Equal(7, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    [Fact]
    public async Task DATA1_TwoExportsInTheSameSecond_ProduceTwoFiles()
    {
        using var t = new TempDb();
        var backup = new BackupService(t.Db, t.Clock, Path.Combine(t.Folder, "exports"), TempDb.Log<BackupService>());
        var first = (await backup.ExportAsync()).Value;
        var second = (await backup.ExportAsync()).Value;   // same FakeClock instant — minute-precision names collided
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task M3_OutOfBandRewind_DeviceCounterFloorsTheMonthCount()
    {
        using var t = new TempDb();
        var quota = new QuotaStore(t.Db);
        var now = t.Clock.Now;
        for (var i = 0; i < 3; i++) await quota.IncrementAsync(now);
        Assert.Equal(3, await quota.UsedThisMonthAsync(now));

        // The DB file is replaced by an older state behind the app's back (file-manager restore):
        // buckets + watermark rewind to 1, but the sibling lifetime counter still says 3.
        await t.Db.RunAsync(c =>
        {
            c.Execute("UPDATE Meta SET Value = '1' WHERE Key LIKE 'quota.count.%'");
            c.Execute("UPDATE Meta SET Value = '1' WHERE Key = 'quota.lifetime_watermark'");
        });
        Assert.Equal(3, await quota.UsedThisMonthAsync(now));   // 1 + (3 − 1): the count cannot go down
        await quota.IncrementAsync(now);
        Assert.Equal(4, await quota.UsedThisMonthAsync(now));
    }

    [Fact]
    public async Task M3_FirstContact_GrandfathersTheDeviceCounter()
    {
        using var t = new TempDb();
        File.WriteAllText(t.Db.DatabasePath + ".lifetime", "40");   // a counter this DB has never seen
        var quota = new QuotaStore(t.Db);
        Assert.Equal(0, await quota.UsedThisMonthAsync(t.Clock.Now));   // no sudden floor from history
        await quota.IncrementAsync(t.Clock.Now);
        Assert.Equal(1, await quota.UsedThisMonthAsync(t.Clock.Now));
    }

    [Fact]
    public async Task SEC6_Export_StampsMetadataAndHmac_TamperBreaksHmac()
    {
        using var t = new TempDb();
        var rice = new Item { TeluguName = "బియ్యం", EnglishName = "Rice", Unit = Unit.Kilogram, Price = new Paise(6200) };
        await t.Items.SaveAsync(rice);
        var backup = new BackupService(t.Db, t.Clock, Path.Combine(t.Folder, "exports"), TempDb.Log<BackupService>());
        var path = (await backup.ExportAsync()).Value;

        var meta = await backup.ReadMetadataAsync(path);
        Assert.NotNull(meta);
        Assert.Equal(t.Clock.Now, meta!.CreatedAt);
        Assert.Equal(1, meta.ItemCount);
        Assert.True(meta.HasHmac);
        Assert.True(meta.HmacValid);

        Tamper(path, "UPDATE Item SET PricePaise = 999999");   // the books are edited after export
        var after = await backup.ReadMetadataAsync(path);
        Assert.NotNull(after);
        Assert.False(after!.HmacValid);
    }

    [Fact]
    public async Task SEC6_LegacyBackupWithoutStamp_ReportsCountsAndNoHmac()
    {
        using var t = new TempDb();
        var rice = new Item { TeluguName = "బియ్యం", EnglishName = "Rice", Unit = Unit.Kilogram, Price = new Paise(6200) };
        await t.Items.SaveAsync(rice);
        var backup = new BackupService(t.Db, t.Clock, Path.Combine(t.Folder, "exports"), TempDb.Log<BackupService>());
        var path = (await backup.ExportAsync()).Value;

        Tamper(path, "DELETE FROM Meta WHERE Key IN ('backup.hmac', 'backup.created_ms')");   // a pre-SEC-6 backup
        var meta = await backup.ReadMetadataAsync(path);
        Assert.NotNull(meta);
        Assert.Null(meta!.CreatedAt);
        Assert.Equal(1, meta.ItemCount);
        Assert.False(meta.HasHmac);
        Assert.False(meta.HmacValid);
    }

    private static void Tamper(string backupPath, string sql)
    {
        using var c = new SQLiteConnection(backupPath);
        c.Execute(sql);
    }

    [Fact]
    public async Task T3_02_RestoreBackupWithTamperedBillTotal_IsRejected()
    {
        using var h = new AppHost();
        var item = await ReadyAsync(h);
        Assert.True((await SellAsync(h, item)).IsSuccess);
        var backupPath = (await h.Sender.Send(new ExportBackupCommand())).Value;

        Tamper(backupPath, "UPDATE Bill SET GrandTotalPaise = GrandTotalPaise + 100");   // hand-edited total
        var result = await h.Sender.Send(new RestoreBackupCommand(backupPath));
        Assert.Equal(ErrorCodes.RestoreInvalidFile, result.Error.Code);
    }

    [Fact]
    public async Task T3_02_RestoreBackupWithRewoundSequence_IsRejected()
    {
        using var h = new AppHost();
        var item = await ReadyAsync(h);
        Assert.True((await SellAsync(h, item)).IsSuccess);
        var backupPath = (await h.Sender.Send(new ExportBackupCommand())).Value;

        Tamper(backupPath, "UPDATE ShopProfile SET NextBillSequence = 1");   // A-000001 already exists
        var result = await h.Sender.Send(new RestoreBackupCommand(backupPath));
        Assert.Equal(ErrorCodes.RestoreInvalidFile, result.Error.Code);
    }

    [Fact]
    public async Task T3_03_RestoreBackupWithOrphanStockMove_IsRejected()
    {
        using var h = new AppHost();
        var item = await ReadyAsync(h);
        Assert.True((await SellAsync(h, item)).IsSuccess);
        var backupPath = (await h.Sender.Send(new ExportBackupCommand())).Value;

        Tamper(backupPath, "INSERT INTO StockMove (Id, ItemId, DeltaMilli, Reason, BillId, Note, Day, CreatedAtMs) " +
            "VALUES ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 1000, 0, NULL, 'orphan', 20261019, 0)");
        var result = await h.Sender.Send(new RestoreBackupCommand(backupPath));
        Assert.Equal(ErrorCodes.RestoreInvalidFile, result.Error.Code);
    }
}
