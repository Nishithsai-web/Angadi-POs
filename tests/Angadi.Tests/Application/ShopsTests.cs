using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Application.Shops;
using Angadi.Domain.Billing;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using Angadi.Infrastructure.Persistence;
using Angadi.Infrastructure.Shops;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>v2 several shops on one phone: registry, isolation, quota across shops, switching, recovery per shop.</summary>
public class ShopsTests
{
    private static Task<Result<FinalizedBill>> SellAsync(AppHost h, Item item) =>
        h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))));

    private static async Task<Item> ReadyAsync(AppHost h, string name)
    {
        var r = await h.Sender.Send(new SaveShopProfileCommand(new ShopProfile { Name = name, UpiId = "shop@okaxis" }));
        Assert.True(r.IsSuccess, r.Error.Code);
        return await h.AddItemAsync(name + " item", 200, GstSlab.Five, stockMilli: 1_000_000, unit: Unit.Piece);
    }

    private static async Task<ShopEntry> AddShopAsync(AppHost h)
    {
        h.Subscription.IsPro = true;
        var added = await h.Sender.Send(new AddShopCommand());
        Assert.True(added.IsSuccess, added.Error.Code);
        return added.Value;
    }

    private static async Task SwitchAsync(AppHost h, string id)
    {
        var r = await h.Sender.Send(new SwitchShopCommand(id));
        Assert.True(r.IsSuccess, r.Error.Code);
    }

    private static async Task<IReadOnlyList<string>> ItemNamesAsync(AppHost h) =>
        (await h.Sender.Send(new GetAllItemsQuery())).Select(i => i.EnglishName).ToList();

    // ───────────────────────────── registry ─────────────────────────────

    [Fact]
    public void Registry_NoFile_IsOneShopOnTheV1Paths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "angadi-shops", Guid.NewGuid().ToString("N"));
        var registry = new ShopRegistry(dir);

        var only = Assert.Single(registry.List());
        Assert.Equal(ShopRegistry.MainId, only.Id);
        Assert.True(only.IsCurrent);
        Assert.Equal(Path.Combine(dir, "angadi.db"), registry.DatabasePath(ShopRegistry.MainId));
        Assert.Equal(Path.Combine(dir, "snapshots"), registry.SnapshotDirectory(ShopRegistry.MainId));
        Assert.False(File.Exists(registry.RegistryPath));   // reading never writes: existing installs untouched
    }

    [Fact]
    public void Registry_DamagedFile_ListsShopFilesFoundOnDisk_AndRejectsBadIds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "angadi-shops", Guid.NewGuid().ToString("N"));
        var registry = new ShopRegistry(dir);
        var id = ShopRegistry.NewId();
        registry.SetCurrent(id, add: true);
        Assert.True(registry.Rename(id, "Pharmacy"));
        Directory.CreateDirectory(Path.Combine(dir, "shops"));
        File.WriteAllText(registry.DatabasePath(id), "");
        File.WriteAllText(registry.RegistryPath, "{ not json");   // lost shop list

        var list = registry.List();
        Assert.Equal(2, list.Count);
        Assert.Equal(ShopRegistry.MainId, list[0].Id);
        Assert.Equal(id, list[1].Id);           // the shop's books are never orphaned
        Assert.True(list[0].IsCurrent);         // unknown current → the first shop
        Assert.Throws<ArgumentException>(() => registry.DatabasePath("../escape"));
    }

    // ───────────────────────────── free tier ─────────────────────────────

    [Fact]
    public async Task AddShop_FreeTier_NeedsPro_AndNothingIsCreated()
    {
        using var h = new AppHost();
        await ReadyAsync(h, "Ramu");

        var r = await h.Sender.Send(new AddShopCommand());

        Assert.Equal(ErrorCodes.ShopNeedsPro, r.Error.Code);
        Assert.Contains("200", h.Services.GetRequiredService<ILocalizer>().Error(r.Error), StringComparison.Ordinal);   // the price, in the shop's language
        Assert.Single((await h.Sender.Send(new ListShopsQuery())).Shops);
        Assert.False(Directory.Exists(Path.Combine(h.Folder, "data", "shops")));
    }

    [Fact]
    public async Task FreeQuota_IsShared_AcrossShops_NeverMultiplied()
    {
        using var h = new AppHost(configure: s => s.Configure<BillingOptions>(o => o.FreeBillsPerMonth = 3));
        var mainItem = await ReadyAsync(h, "Ramu");
        Assert.True((await SellAsync(h, mainItem)).IsSuccess);
        Assert.True((await SellAsync(h, mainItem)).IsSuccess);

        var second = await AddShopAsync(h);
        var secondItem = await ReadyAsync(h, "Lakshmi");
        h.Subscription.IsPro = false;                       // Pro lapsed with two shops on the phone
        Assert.True((await SellAsync(h, secondItem)).IsSuccess);

        Assert.Equal(3, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
        Assert.Equal(ErrorCodes.FreeLimitReached, (await SellAsync(h, secondItem)).Error.Code);

        await SwitchAsync(h, ShopRegistry.MainId);
        Assert.Equal(3, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
        Assert.Equal(ErrorCodes.FreeLimitReached, (await SellAsync(h, mainItem)).Error.Code);
        Assert.NotEqual(ShopRegistry.MainId, second.Id);
    }

    // ───────────────────────────── isolation ─────────────────────────────

    [Fact]
    public async Task Shops_KeepTheirOwnBooks_Draft_AndSafetyCopies()
    {
        using var h = new AppHost();
        await ReadyAsync(h, "Ramu");
        var drafts = h.Services.GetRequiredService<IBillDraftStore>();
        await drafts.SaveAsync("{\"main\":1}");

        var second = await AddShopAsync(h);
        Assert.Null(await h.Sender.Send(new GetShopProfileQuery()));   // the shell opens normal shop setup
        Assert.Empty(await ItemNamesAsync(h));
        Assert.Null(await drafts.LoadAsync());                         // the unfinished bill stays with its shop
        await ReadyAsync(h, "Lakshmi");
        await drafts.SaveAsync("{\"second\":1}");
        Assert.True((await h.Services.GetRequiredService<IDataSafety>().SnapshotAsync()).IsSuccess);

        await SwitchAsync(h, ShopRegistry.MainId);
        Assert.Equal("Ramu", (await h.Sender.Send(new GetShopProfileQuery()))!.Name);
        Assert.Equal(new[] { "Ramu item" }, await ItemNamesAsync(h));
        Assert.Equal("{\"main\":1}", await drafts.LoadAsync());

        await SwitchAsync(h, second.Id);
        Assert.Equal(new[] { "Lakshmi item" }, await ItemNamesAsync(h));
        Assert.Equal("{\"second\":1}", await drafts.LoadAsync());

        var data = Path.Combine(h.Folder, "data");
        Assert.True(File.Exists(Path.Combine(data, "angadi.db")));
        Assert.True(File.Exists(Path.Combine(data, "shops", second.Id + ".db")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(data, "shop-snapshots", second.Id), "angadi-*.db"));

        var list = await h.Sender.Send(new ListShopsQuery());
        Assert.True(list.HasSeveral);
        Assert.Equal("Lakshmi", list.Current.Name);                    // list name follows the profile
    }

    [Fact]
    public async Task Rename_RequiresAName_AndRenamesInTheList()
    {
        using var h = new AppHost();
        await ReadyAsync(h, "Ramu");
        Assert.Equal(ErrorCodes.ShopNameRequired, (await h.Sender.Send(new RenameShopCommand(ShopRegistry.MainId, "  "))).Error.Code);
        Assert.Equal(ErrorCodes.ShopNotFound, (await h.Sender.Send(new RenameShopCommand(ShopRegistry.NewId(), "X"))).Error.Code);
        Assert.True((await h.Sender.Send(new RenameShopCommand(ShopRegistry.MainId, " Ramu Kirana "))).IsSuccess);
        Assert.Equal("Ramu Kirana", (await h.Sender.Send(new ListShopsQuery())).Current.Name);
    }

    [Fact]
    public async Task Switch_CarriesTheDeviceLanguage_ToTheOpenedShop()
    {
        using var h = new AppHost();
        await ReadyAsync(h, "Ramu");                                   // profile language: Telugu (default)
        await AddShopAsync(h);
        await ReadyAsync(h, "Lakshmi");
        h.Services.GetRequiredService<ILocalizer>().Language = UiLanguage.English;

        await SwitchAsync(h, ShopRegistry.MainId);

        Assert.Equal(UiLanguage.English, (await h.Sender.Send(new GetShopProfileQuery()))!.Language);
    }

    [Fact]
    public async Task Switch_UnknownShop_IsRefused()
    {
        using var h = new AppHost();
        Assert.Equal(ErrorCodes.ShopNotFound, (await h.Sender.Send(new SwitchShopCommand(ShopRegistry.NewId()))).Error.Code);
    }

    // ───────────────────────────── switch under load / failure ─────────────────────────────

    [Fact]
    public async Task SwitchTo_OpenFails_StaysOnTheShop_AndBothLocksAreReleased()
    {
        using var db = new TempDb();
        await db.Db.RunAsync(c => c.InsertOrReplace(new MetaRow { Key = "probe", Value = "first" }));
        var before = db.Db.DatabasePath;
        var blocker = Path.Combine(db.Folder, "blocker");
        File.WriteAllText(blocker, "a file where the folder should be");

        await Assert.ThrowsAnyAsync<Exception>(() => db.Db.SwitchToAsync(Path.Combine(blocker, "x.db"), Path.Combine(db.Folder, "snaps-x")));

        Assert.Equal(before, db.Db.DatabasePath);
        var read = db.Db.ReadAsync(c => c.Find<MetaRow>("probe")?.Value);
        var write = db.Db.RunAsync(c => c.Find<MetaRow>("probe")?.Value);
        await Task.WhenAny(Task.WhenAll(read, write), Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(read.IsCompleted && write.IsCompleted, "a lock was left held after the failed switch");
        Assert.Equal("first", await read);
        Assert.Equal("first", await write);
        Assert.Equal(0, db.Db.Generation);
    }

    [Fact]
    public async Task SwitchTo_UnderLoad_EveryReadAndWriteLandsInOneFile_NoDeadlock()
    {
        using var db = new TempDb();
        var first = db.Db.DatabasePath;
        var second = Path.Combine(db.Folder, "shops", ShopRegistry.NewId() + ".db");
        var snaps = Path.Combine(db.Folder, "snaps-2");
        var firstSnaps = db.Db.SnapshotDirectory;

        using var stop = new CancellationTokenSource();
        var workers = Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                await db.Db.RunAsync(c => c.InsertOrReplace(new MetaRow { Key = "w" + i, Value = n.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                Assert.Equal(AngadiDb.AppMarker, await db.Db.ReadAsync(c => c.Find<MetaRow>("app")?.Value));
                n++;
            }
            return n;
        })).ToList();

        for (var k = 0; k < 10; k++)
        {
            await db.Db.SwitchToAsync(k % 2 == 0 ? second : first, k % 2 == 0 ? snaps : firstSnaps);
            await Task.Delay(20);
        }
        await stop.CancelAsync();
        var counts = await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.All(counts, c => Assert.True(c > 0));
        Assert.Equal(10, db.Db.Generation);
        Assert.Equal(first, db.Db.DatabasePath);
    }

    // ───────────────────────────── recovery per shop ─────────────────────────────

    [Fact]
    public async Task DamagedShopFile_IsRecovered_FromThatShopsOwnSafetyCopy()
    {
        using var h = new AppHost();
        await ReadyAsync(h, "Ramu");
        var second = await AddShopAsync(h);
        await ReadyAsync(h, "Lakshmi");
        Assert.True((await h.Services.GetRequiredService<IDataSafety>().SnapshotAsync()).IsSuccess);
        await SwitchAsync(h, ShopRegistry.MainId);

        // A power cut tears the closed shop's file.
        var path = Path.Combine(h.Folder, "data", "shops", second.Id + ".db");
        foreach (var suffix in new[] { "-wal", "-shm" }) File.Delete(path + suffix);
        await using (var f = new FileStream(path, FileMode.Open, FileAccess.Write))
            await f.WriteAsync(Enumerable.Repeat((byte)0x5A, 4096).ToArray());

        await SwitchAsync(h, second.Id);

        Assert.Equal(new[] { "Lakshmi item" }, await ItemNamesAsync(h));
        Assert.NotNull(await h.Sender.Send(new GetDataRecoveryQuery()));          // told once, on that shop
        Assert.Single(Directory.GetFiles(Path.Combine(h.Folder, "data", "shops"), second.Id + ".db.damaged-*"));
        await SwitchAsync(h, ShopRegistry.MainId);
        Assert.Equal(new[] { "Ramu item" }, await ItemNamesAsync(h));                     // the first shop is untouched
    }
}
