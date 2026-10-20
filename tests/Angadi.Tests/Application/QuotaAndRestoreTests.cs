using Angadi.Application.Billing;
using Angadi.Application.Common;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Persistence;

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
        h.Clock.Now = new DateTimeOffset(2026, 11, 10, 10, 0, 0, Ist);
        var item = await ReadyAsync(h);
        for (var i = 0; i < 50; i++) Assert.True((await SellAsync(h, item)).IsSuccess);

        h.Clock.Now = new DateTimeOffset(2026, 10, 15, 10, 0, 0, Ist);   // shopkeeper sets the phone back a month
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
        Assert.Equal(1, quota.UsedThisMonth);                                     // effective month stays Jan, not a fresh 0
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
}
