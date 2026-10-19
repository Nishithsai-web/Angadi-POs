using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using Angadi.Domain.Events;
using Angadi.Infrastructure.Persistence;
using Angadi.Infrastructure.Seed;
using SQLite;

namespace Angadi.Tests.Infrastructure;

public class DataLayerTests : IDisposable
{
    private readonly TempDb _t = new();
    public void Dispose() => _t.Dispose();

    private static Item Rice(string? barcode = "8901234567890") => new()
    {
        TeluguName = "బియ్యం", EnglishName = "Rice", Aliases = ["biyyam"], Unit = Unit.Kilogram,
        Price = new Paise(6200), GstSlab = GstSlab.Zero, Barcode = barcode, LowStockThreshold = Qty.Whole(5),
    };

    [Fact]
    public async Task FR03_Item_RoundTrips_WithAliasesAndBarcode()
    {
        var rice = Rice();
        await _t.Items.SaveAsync(rice);

        var loaded = await _t.Items.GetAsync(rice.Id);
        Assert.NotNull(loaded);
        Assert.Equal("బియ్యం", loaded.TeluguName);
        Assert.Equal(new[] { "biyyam" }, loaded.Aliases);
        Assert.Equal(6200, loaded.Price.Value);

        var byBarcode = await _t.Items.GetByBarcodeAsync(" 8901234567890 ");
        Assert.Equal(rice.Id, byBarcode?.Id);
    }

    [Fact]
    public async Task FR08_Barcode_IsUnique_ButBlankBarcodesAreAllowedTwice()
    {
        await _t.Items.SaveAsync(Rice());
        await Assert.ThrowsAsync<SQLiteException>(() => _t.Items.SaveAsync(Rice()));

        await _t.Items.SaveAsync(Rice(barcode: ""));
        await _t.Items.SaveAsync(Rice(barcode: null));
    }

    [Fact]
    public async Task FR05_Search_FindsByTeluguEnglishAliasAndBarcodePrefix()
    {
        await _t.Items.SaveAsync(Rice());
        Assert.Single(await _t.Items.SearchAsync("బియ్"));
        Assert.Single(await _t.Items.SearchAsync("ric"));
        Assert.Single(await _t.Items.SearchAsync("biyy"));
        Assert.Single(await _t.Items.SearchAsync("890123"));
        Assert.Empty(await _t.Items.SearchAsync("sugar"));
    }

    [Fact]
    public async Task FR24_SavingAnItem_NeverOverwritesStock()
    {
        var rice = Rice();
        await _t.Items.SaveAsync(rice);
        await _t.Ledger.AdjustAsync(rice.Id, Qty.Whole(10), StockMoveReason.Purchase);

        rice.Price = new Paise(6500);   // stale object still has Stock = 0
        await _t.Items.SaveAsync(rice);

        Assert.Equal(10_000, (await _t.Items.GetAsync(rice.Id))!.Stock.Milli);
        Assert.Equal(10_000, rice.Stock.Milli);
    }

    [Fact]
    public async Task NFR06_ConcurrentAdjusts_LoseNoUpdates_AndLedgerReconciles()
    {
        var rice = Rice();
        await _t.Items.SaveAsync(rice);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            _t.Ledger.AdjustAsync(rice.Id, new Qty(i % 2 == 0 ? 1500 : -500), StockMoveReason.CountCorrection)));

        var stock = (await _t.Items.GetAsync(rice.Id))!.Stock.Milli;
        var history = await _t.Ledger.GetHistoryAsync(rice.Id);
        Assert.Equal(100 * 1500 - 100 * 500, stock);
        Assert.Equal(200, history.Count);
        Assert.Equal(stock, history.Sum(m => m.Delta.Milli));
    }

    [Fact]
    public async Task FR25_LowStock_ListsItemsAtOrBelowThreshold()
    {
        var rice = Rice();
        await _t.Items.SaveAsync(rice);
        await _t.Ledger.AdjustAsync(rice.Id, Qty.Whole(5), StockMoveReason.Opening);
        Assert.Single(await _t.Items.GetLowStockAsync());

        await _t.Ledger.AdjustAsync(rice.Id, Qty.Whole(1), StockMoveReason.Purchase);
        Assert.Empty(await _t.Items.GetLowStockAsync());
    }

    [Fact]
    public async Task FR15_UnitOfWork_RollsBack_OnFailedResult_AndOnException()
    {
        var rice = Rice();
        await _t.Items.SaveAsync(rice);

        await _t.Db.ExecuteAsync(async () =>
        {
            await _t.Ledger.AdjustAsync(rice.Id, Qty.Whole(3), StockMoveReason.Purchase);
            return false;
        }, ok => ok);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _t.Db.ExecuteAsync<bool>(async () =>
        {
            await _t.Ledger.AdjustAsync(rice.Id, Qty.Whole(3), StockMoveReason.Purchase);
            throw new InvalidOperationException("boom");
        }, _ => true));

        Assert.Equal(0, (await _t.Items.GetAsync(rice.Id))!.Stock.Milli);
        Assert.Empty(await _t.Ledger.GetHistoryAsync(rice.Id));

        await _t.Db.ExecuteAsync(async () =>
        {
            await _t.Ledger.AdjustAsync(rice.Id, Qty.Whole(3), StockMoveReason.Purchase);
            return true;
        }, ok => ok);
        Assert.Equal(3000, (await _t.Items.GetAsync(rice.Id))!.Stock.Milli);
    }

    [Fact]
    public async Task FR15_Bill_RoundTrips_WithLinesAndTaxSplits()
    {
        var line = new BillLine
        {
            ItemId = Guid.NewGuid(), TeluguName = "బియ్యం", EnglishName = "Rice", Unit = Unit.Kilogram,
            UnitPrice = new Paise(6000), GstSlab = GstSlab.Five, Qty = Qty.Whole(2), Source = LineSource.Voice,
        };
        var bill = new Bill { Number = "A-000001", Lines = [line], Tender = TenderType.Upi, Status = BillStatus.Finalized, FinalizedAt = _t.Clock.Now };
        BillCalculator.Compute(bill, shopHasGstin: true);
        await _t.Bills.SaveAsync(bill);

        var loaded = await _t.Bills.GetAsync(bill.Id);
        Assert.NotNull(loaded);
        Assert.Equal(12000, loaded.GrandTotal.Value);
        Assert.Equal(TenderType.Upi, loaded.Tender);
        var split = Assert.Single(loaded.TaxSplits);
        Assert.Equal((GstSlab.Five, 11429L, 571L), (split.Slab, split.Taxable.Value, split.Tax.Value));
        var l = Assert.Single(loaded.Lines);
        Assert.Equal(LineSource.Voice, l.Source);
        Assert.Equal(TimeSpan.FromHours(5.5), loaded.FinalizedAt!.Value.Offset);

        Assert.Equal(1, await _t.Bills.CountFinalizedInMonthAsync(2026, 10));
        Assert.Single(await _t.Bills.ListByDayAsync(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 19)));
    }

    [Fact]
    public async Task FR01_BillNumbers_AreSequentialWithPrefix()
    {
        await _t.Shop.SaveAsync(new ShopProfile { Name = "రాము కిరాణా", UpiId = "ramu@okaxis", BillPrefix = "R" });
        Assert.Equal("R-000001", await _t.Shop.NextBillNumberAsync());
        Assert.Equal("R-000002", await _t.Shop.NextBillNumberAsync());

        await _t.Shop.SaveAsync(new ShopProfile { Name = "Renamed", UpiId = "ramu@okaxis", BillPrefix = "R" });
        Assert.Equal("R-000003", await _t.Shop.NextBillNumberAsync());   // edit doesn't reset the sequence
        Assert.Equal("Renamed", (await _t.Shop.GetAsync())!.Name);
    }

    [Fact]
    public async Task FR22_DuesEntries_MoveBalance_AndCustomerEditKeepsIt()
    {
        var c = new Customer { Name = "లక్ష్మి", Phone = "919876543210" };
        await _t.Customers.SaveAsync(c);
        await _t.Customers.AddDuesEntryAsync(new DuesEntry { CustomerId = c.Id, Kind = DuesEntryKind.Charge, Amount = new Paise(25000) });
        await _t.Customers.AddDuesEntryAsync(new DuesEntry { CustomerId = c.Id, Kind = DuesEntryKind.Payment, Amount = new Paise(10000) });

        c.Name = "లక్ష్మి గారు";
        await _t.Customers.SaveAsync(c);   // stale DuesBalance = 0 must not win

        var loaded = await _t.Customers.GetAsync(c.Id);
        Assert.Equal(15000, loaded!.DuesBalance.Value);
        Assert.Equal(2, (await _t.Customers.GetLedgerAsync(c.Id)).Count);
        Assert.Single(await _t.Customers.GetWithDuesAsync());
    }

    [Fact]
    public async Task FR31_DailySales_SumsSalesMinusVoidRestores()
    {
        var rice = Rice();
        await _t.Items.SaveAsync(rice);
        await _t.Ledger.AdjustAsync(rice.Id, Qty.Whole(50), StockMoveReason.Opening);
        await _t.Ledger.AdjustAsync(rice.Id, new Qty(-2000), StockMoveReason.Sale);
        await _t.Ledger.AdjustAsync(rice.Id, new Qty(-3000), StockMoveReason.Sale);
        await _t.Ledger.AdjustAsync(rice.Id, new Qty(2000), StockMoveReason.VoidRestore);
        _t.Clock.Advance(TimeSpan.FromDays(1));
        await _t.Ledger.AdjustAsync(rice.Id, new Qty(-1000), StockMoveReason.Sale);

        var sales = await _t.Ledger.GetDailySalesAsync(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 20));
        Assert.Equal(3000, sales.Single(s => s.Day == new DateOnly(2026, 10, 19)).Sold.Milli);
        Assert.Equal(1000, sales.Single(s => s.Day == new DateOnly(2026, 10, 20)).Sold.Milli);
    }

    [Fact]
    public async Task NFR05_Outbox_AppendsPendingRow()
    {
        await _t.Outbox.AppendAsync("Bill", new BillFinalized(Guid.NewGuid(), _t.Clock.Now, 12000));
        var count = await _t.Db.RunAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Outbox WHERE SyncState = 0 AND EventType = 'BillFinalized'"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task FR04_Seed_IsIdempotent_AndOpeningStockGoesThroughLedger()
    {
        var seed = new SeedService(_t.Db, _t.Items, _t.Ledger, TempDb.Log<SeedService>());
        Assert.Equal(120, await seed.SeedAsync(kirana: true, pharmacy: true));
        Assert.Equal(0, await seed.SeedAsync(kirana: true, pharmacy: true));

        var all = await _t.Items.GetActiveAsync();
        Assert.Equal(120, all.Count);
        var rice = all.Single(i => i.EnglishName.StartsWith("Rice"));
        Assert.Equal(20_000, rice.Stock.Milli);
        Assert.Single(await _t.Ledger.GetHistoryAsync(rice.Id));
        Assert.Equal(36, (await _t.Items.GetQuickSaleAsync()).Count);
    }

    [Fact]
    public async Task FR37_Backup_Exports_AndRestoreRejectsForeignFiles()
    {
        var rice = Rice();
        await _t.Items.SaveAsync(rice);
        var backup = new BackupService(_t.Db, _t.Clock, Path.Combine(_t.Folder, "exports"), TempDb.Log<BackupService>());

        var exported = await backup.ExportAsync();
        Assert.True(exported.IsSuccess);
        Assert.True(BackupService.IsAngadiBackup(exported.Value, out _));

        var junk = Path.Combine(_t.Folder, "junk.db");
        await File.WriteAllTextAsync(junk, "not a database");
        var rejected = await backup.RestoreAsync(junk);
        Assert.Equal("RESTORE_INVALID_FILE", rejected.Error.Code);
    }

    [Fact]
    public async Task FR37_Restore_BringsBackOldData()
    {
        var rice = Rice();
        await _t.Items.SaveAsync(rice);
        var backup = new BackupService(_t.Db, _t.Clock, Path.Combine(_t.Folder, "exports"), TempDb.Log<BackupService>());
        var snapshot = (await backup.ExportAsync()).Value;

        _t.Clock.Advance(TimeSpan.FromMinutes(5));   // distinct file name for the safety copy
        await _t.Items.SaveAsync(new Item { TeluguName = "చక్కెర", EnglishName = "Sugar", Unit = Unit.Kilogram, Price = new Paise(4600) });
        Assert.Equal(2, (await _t.Items.GetActiveAsync()).Count);

        Assert.True((await backup.RestoreAsync(snapshot)).IsSuccess);
        Assert.Single(await _t.Items.GetActiveAsync());
    }
}
