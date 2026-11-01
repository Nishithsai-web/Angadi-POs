using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Inventory;
using Angadi.Application.Stock;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Purchasing;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>
/// v2-stock: purchase entry, batches and FEFO through the REAL handler graph. The invariants are checked straight
/// against the database after every step: Item.Stock == Σ StockMoves; for batch-tracked items Σ batch qty == Item.Stock,
/// each batch == Σ the moves that name it, and every move of such an item carries its BatchId.
/// </summary>
public sealed class StockBatchTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private AngadiDb Db => _h.Services.GetRequiredService<AngadiDb>();

    private async Task<Item> TrackedAsync(string name, long pricePaise = 5000, long openingMilli = 0)
    {
        var item = new Item { TeluguName = name, EnglishName = name, Unit = Unit.Strip, Price = new Paise(pricePaise), GstSlab = GstSlab.Five, TracksBatches = true };
        var r = await _h.Sender.Send(new SaveItemCommand(item, openingMilli > 0 ? new Qty(openingMilli) : null));
        Assert.True(r.IsSuccess, r.Error.Code);
        return item;
    }

    private async Task<Guid> SupplierAsync(string name = "Sri Pharma")
    {
        var r = await _h.Sender.Send(new SaveSupplierCommand(new Supplier { Name = name, Phone = "9876543210" }));
        Assert.True(r.IsSuccess, r.Error.Code);
        return r.Value;
    }

    private static int _bill;
    private Task<Result<Guid>> BuyAsync(Guid supplier, params PurchaseEntryLine[] lines) =>
        _h.Sender.Send(new RecordPurchaseCommand(new PurchaseEntry(supplier, "INV-" + Interlocked.Increment(ref _bill), _h.Clock.Today, lines)));

    private static PurchaseEntryLine Line(Item item, long milli, long costPaise, string? batch = null, DateOnly? expiry = null) =>
        new(item.Id, new Qty(milli), new Paise(costPaise), GstSlab.Five, batch, expiry);

    private Task<Result<FinalizedBill>> SellAsync(Item item, long milli, bool confirmExpired = false) =>
        _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, milli))) { ExpiredBatchConfirmed = confirmExpired });

    private async Task<IReadOnlyList<Batch>> BatchesAsync(Item item) => await _h.Sender.Send(new GetItemBatchesQuery(item.Id));

    private async Task AssertInvariantsAsync(string context, bool trackedFromStart = true)
    {
        var problems = await Db.RunAsync(c =>
        {
            var list = new List<string>();
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Item i WHERE i.StockMilli <> (SELECT COALESCE(SUM(DeltaMilli), 0) FROM StockMove m WHERE m.ItemId = i.Id)") > 0)
                list.Add("stock != Σ moves");
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Item i WHERE i.TracksBatches = 1 AND i.StockMilli <> (SELECT COALESCE(SUM(QtyMilli), 0) FROM Batch b WHERE b.ItemId = i.Id)") > 0)
                list.Add("stock != Σ batches");
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Batch b WHERE b.QtyMilli <> (SELECT COALESCE(SUM(DeltaMilli), 0) FROM StockMove m WHERE m.BatchId = b.Id)") > 0)
                list.Add("batch != Σ its moves");
            if (trackedFromStart && c.ExecuteScalar<int>("SELECT COUNT(*) FROM StockMove m JOIN Item i ON i.Id = m.ItemId WHERE i.TracksBatches = 1 AND m.BatchId IS NULL") > 0)
                list.Add("tracked move without BatchId");
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Batch WHERE QtyMilli < 0 AND BatchNo <> ''") > 0)
                list.Add("named batch below zero");
            if (!BackupService.BatchesReconcile(c, out var reason)) list.Add("restore sweep: " + reason);
            return list;
        });
        Assert.Equal(context + ": ", context + ": " + string.Join("; ", problems));
        var report = await _h.Sender.Send(new GetInventoryReportQuery());
        Assert.True(report.LedgerMatches, context + ": inventory report ledger check");
        Assert.Equal(0, (await _h.Sender.Send(new GetStockValuationQuery())).BatchMismatches);
    }

    [Fact]
    public async Task Purchase_CreatesBatches_SavesCost_LinksMoves_AndIsOneTransaction()
    {
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync();
        var supplier = await SupplierAsync();
        var dolo = await TrackedAsync("Dolo 650");
        var rice = await _h.AddItemAsync("Rice", 6000, stockMilli: 0);
        var exp = _h.Clock.Today.AddMonths(8);

        var r = await BuyAsync(supplier, Line(dolo, 10_000, 2500, "b12", exp), Line(rice, 25_000, 4800));
        Assert.True(r.IsSuccess, r.Error.Code);

        Assert.Equal(10_000, await _h.StockAsync(dolo.Id));
        Assert.Equal(25_000, await _h.StockAsync(rice.Id));
        var batch = Assert.Single(await BatchesAsync(dolo));
        Assert.Equal("B12", batch.BatchNo);   // stored upper-case: "b12" and "B12" are the same printed batch
        Assert.Equal(exp, batch.Expiry);
        Assert.Equal(2500, batch.Cost.Value);
        Assert.Equal(2500, (await _h.Sender.Send(new GetItemQuery(dolo.Id)))!.Cost.Value);
        Assert.Equal(4800, (await _h.Sender.Send(new GetItemQuery(rice.Id)))!.Cost.Value);

        var purchase = (await _h.Sender.Send(new GetPurchaseQuery(r.Value)))!;
        Assert.Equal(2, purchase.Lines.Count);
        Assert.Equal(batch.Id, purchase.Lines[0].BatchId);
        // 10 × ₹25 × 1.05 = ₹262.50; 25 × ₹48 × 1.05 = ₹1,260.00
        Assert.Equal(26_250 + 126_000, purchase.Total.Value);
        var linked = await Db.RunAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM StockMove WHERE PurchaseId = ? AND Reason = ?", r.Value, (int)StockMoveReason.Purchase));
        Assert.Equal(2, linked);

        // The same supplier bill twice would double the stock: refused, and nothing changes.
        var again = await _h.Sender.Send(new RecordPurchaseCommand(new PurchaseEntry(supplier, purchase.SupplierBillNo.ToLowerInvariant(), _h.Clock.Today,
            [Line(rice, 1000, 4800)])));
        Assert.Equal(ErrorCodes.PurchaseDuplicate, again.Error.Code);
        Assert.Equal(25_000, await _h.StockAsync(rice.Id));

        // A batch-tracked line without batch details fails the WHOLE purchase: the good line before it is rolled back.
        var bad = await BuyAsync(supplier, Line(rice, 5000, 4800), Line(dolo, 1000, 2500));
        Assert.Equal(ErrorCodes.BatchDetailsRequired, bad.Error.Code);
        Assert.Equal(25_000, await _h.StockAsync(rice.Id));
        await AssertInvariantsAsync("after purchases");
    }

    [Fact]
    public async Task Sale_SplitsFefo_AcrossBatches_VoidPutsBackWhereItCameFrom_AndReceiptShowsBatches()
    {
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync();
        var supplier = await SupplierAsync();
        var item = await TrackedAsync("Amoxyclav");
        var today = _h.Clock.Today;
        Assert.True((await BuyAsync(supplier,
            Line(item, 5000, 1000, "LATE", today.AddMonths(12)),
            Line(item, 3000, 1000, "SOON", today.AddMonths(2)),
            Line(item, 4000, 1000, "MID", today.AddMonths(6)))).IsSuccess);

        var sale = await SellAsync(item, 9000);   // 3 SOON + 4 MID + 2 LATE
        Assert.True(sale.IsSuccess, sale.Error.Code);
        var after = (await BatchesAsync(item)).ToDictionary(b => b.BatchNo, b => b.Qty.Milli);
        Assert.Equal(0, after["SOON"]);
        Assert.Equal(0, after["MID"]);
        Assert.Equal(3000, after["LATE"]);

        var bill = (await _h.Sender.Send(new GetBillQuery(sale.Value.BillId)))!;
        Assert.Equal(new[] { "SOON", "MID", "LATE" }, bill.Lines[0].Batches.Select(b => b.BatchNo));
        Assert.Equal(today.AddMonths(2).Month, bill.Lines[0].Batches[0].Expiry!.Value.Month);
        await AssertInvariantsAsync("after FEFO sale");

        Assert.True((await _h.Sender.Send(new VoidBillCommand(sale.Value.BillId, "test"))).IsSuccess);
        after = (await BatchesAsync(item)).ToDictionary(b => b.BatchNo, b => b.Qty.Milli);
        Assert.Equal(3000, after["SOON"]);
        Assert.Equal(4000, after["MID"]);
        Assert.Equal(5000, after["LATE"]);
        Assert.Equal(12_000, await _h.StockAsync(item.Id));
        await AssertInvariantsAsync("after void");
    }

    [Fact]
    public async Task ExpiredBatch_IsNeverSoldSilently_OwnerConfirmationSellsIt()
    {
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync();
        var supplier = await SupplierAsync();
        var item = await TrackedAsync("Cough syrup");
        var today = _h.Clock.Today;
        Assert.True((await BuyAsync(supplier, Line(item, 2000, 9000, "OLD", today.AddDays(10)), Line(item, 3000, 9000, "NEW", today.AddMonths(9)))).IsSuccess);
        _h.Clock.Advance(TimeSpan.FromDays(30));   // OLD is now expired

        // Within the unexpired stock: FEFO skips the expired batch, no question asked.
        var fine = await SellAsync(item, 3000);
        Assert.True(fine.IsSuccess, fine.Error.Code);
        Assert.Equal(2000, (await BatchesAsync(item)).Single(b => b.BatchNo == "OLD").Qty.Milli);

        // Only expired stock is left: the finalize guard refuses, stock and bill sequence untouched.
        Assert.Single(await _h.Sender.Send(new CheckExpiredSaleQuery([new SaleQty(item.Id, Qty.Whole(1))])));
        var refused = await SellAsync(item, 1000);
        Assert.Equal(ErrorCodes.BatchExpired, refused.Error.Code);
        Assert.Equal(2000, await _h.StockAsync(item.Id));
        Assert.True((await _h.Sender.Send(new GetExpiringQuery())).HasAlerts);

        // The owner confirmed (UI: PIN gate) → it sells from the expired batch.
        var confirmed = await SellAsync(item, 1000, confirmExpired: true);
        Assert.True(confirmed.IsSuccess, confirmed.Error.Code);
        Assert.Equal(1000, await _h.StockAsync(item.Id));
        await AssertInvariantsAsync("after confirmed expired sale");

        // Items without batches never meet the check.
        var plain = await _h.AddItemAsync("Soap", 4000);
        Assert.Empty(await _h.Sender.Send(new CheckExpiredSaleQuery([new SaleQty(plain.Id, Qty.Whole(1))])));
        Assert.True((await SellAsync(plain, 1000)).IsSuccess);
    }

    [Fact]
    public async Task SwitchingTrackingOn_PutsExistingStockInNoBatchRow_AndInvariantsHold()
    {
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync();
        var item = await _h.AddItemAsync("Crocin", 3000, stockMilli: 7000, unit: Unit.Strip);
        Assert.True((await SellAsync(item, 1000)).IsSuccess);   // a v1 sale: no batch involved

        var fresh = (await _h.Sender.Send(new GetItemQuery(item.Id)))!;
        fresh.TracksBatches = true;
        Assert.True((await _h.Sender.Send(new SaveItemCommand(fresh))).IsSuccess);
        var placeholder = Assert.Single(await BatchesAsync(item));
        Assert.True(placeholder.IsPlaceholder);
        Assert.Equal(6000, placeholder.Qty.Milli);
        Assert.Equal(6000, await _h.StockAsync(item.Id));

        // Saving again (an ordinary edit) changes nothing.
        fresh = (await _h.Sender.Send(new GetItemQuery(item.Id)))!;
        fresh.Price = new Paise(3200);
        Assert.True((await _h.Sender.Send(new SaveItemCommand(fresh))).IsSuccess);
        Assert.Single(await BatchesAsync(item));

        var supplier = await SupplierAsync();
        Assert.True((await BuyAsync(supplier, Line(item, 4000, 2000, "X1", _h.Clock.Today.AddMonths(3)))).IsSuccess);
        Assert.True((await SellAsync(item, 5000)).IsSuccess);   // X1 (has an expiry) goes before the no-expiry row
        var batches = (await BatchesAsync(item)).ToDictionary(b => b.BatchNo, b => b.Qty.Milli);
        Assert.Equal(0, batches["X1"]);
        Assert.Equal(5000, batches[""]);
        await AssertInvariantsAsync("after tracking switched on", trackedFromStart: false);
    }

    [Fact]
    public async Task BatchAdjustments_PickTheBatch_NeverGoBelowZero_PurchaseCorrectionIsBounded()
    {
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync();
        var supplier = await SupplierAsync();
        var item = await TrackedAsync("Insulin");
        var p = await BuyAsync(supplier, Line(item, 5000, 40000, "A1", _h.Clock.Today.AddMonths(4)), Line(item, 5000, 40000, "A2", _h.Clock.Today.AddMonths(5)));
        Assert.True(p.IsSuccess);
        var a2 = (await BatchesAsync(item)).Single(b => b.BatchNo == "A2");

        var damage = await _h.Sender.Send(new AdjustBatchStockCommand(item.Id, a2.Id, Qty.Whole(-2), StockMoveReason.Damage));
        Assert.True(damage.IsSuccess, damage.Error.Code);
        var tooMuch = await _h.Sender.Send(new AdjustBatchStockCommand(item.Id, a2.Id, Qty.Whole(-4), StockMoveReason.Damage));
        Assert.Equal(ErrorCodes.BatchQtyTooLow, tooMuch.Error.Code);
        Assert.Equal(ErrorCodes.ValidationFailed,
            (await _h.Sender.Send(new AdjustBatchStockCommand(item.Id, a2.Id, Qty.Whole(-1), StockMoveReason.Sale))).Error.Code);

        var purchase = (await _h.Sender.Send(new GetPurchaseQuery(p.Value)))!;
        var lineA1 = purchase.Lines.Single(l => l.BatchNo == "A1");
        Assert.True((await _h.Sender.Send(new CorrectPurchaseLineCommand(purchase.Id, lineA1.Id, Qty.Whole(2)))).IsSuccess);
        var over = await _h.Sender.Send(new CorrectPurchaseLineCommand(purchase.Id, lineA1.Id, Qty.Whole(4)));
        Assert.Equal(ErrorCodes.PurchaseCorrectionTooLarge, over.Error.Code);   // 5 bought, 2 already taken back
        Assert.True((await _h.Sender.Send(new CorrectPurchaseLineCommand(purchase.Id, lineA1.Id, Qty.Whole(3)))).IsSuccess);
        Assert.Equal(0, (await BatchesAsync(item)).Single(b => b.BatchNo == "A1").Qty.Milli);
        Assert.Equal(3000, await _h.StockAsync(item.Id));

        // Stock-in a new batch from the item screen (no supplier bill).
        Assert.True((await _h.Sender.Send(new StockInBatchCommand(item.Id, "a3", _h.Clock.Today.AddMonths(7), Qty.Whole(6)))).IsSuccess);
        Assert.Equal(9000, await _h.StockAsync(item.Id));
        await AssertInvariantsAsync("after adjustments");
    }

    [Fact]
    public async Task PurchaseOrder_SuggestShareReceive_BecomesAPurchase()
    {
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync();
        var supplier = await SupplierAsync("Ravi Agencies");
        var low = await _h.AddItemAsync("Sugar", 4500, stockMilli: 1000);
        low.LowStockThreshold = Qty.Whole(5);
        Assert.True((await _h.Sender.Send(new SaveItemCommand(low))).IsSuccess);

        var suggested = await _h.Sender.Send(new SuggestOrderFromLowStockQuery());
        var line = Assert.Single(suggested, l => l.ItemId == low.Id);
        Assert.Equal(9000, line.Qty.Milli);   // up to 2 × the low mark (10) from 1 in stock, whole units

        var order = new PurchaseOrder { SupplierId = supplier, Lines = [line, new PurchaseOrderLine(low.Id, Qty.Whole(1))] };
        var saved = await _h.Sender.Send(new SavePurchaseOrderCommand(order));
        Assert.True(saved.IsSuccess, saved.Error.Code);
        Assert.Equal(10_000, Assert.Single((await _h.Sender.Send(new GetPurchaseOrderQuery(saved.Value)))!.Lines).Qty.Milli);   // merged

        Assert.True((await _h.Sender.Send(new SharePurchaseOrderCommand(saved.Value))).IsSuccess);
        Assert.Contains("Sugar", _h.Share.SharedText.Last(), StringComparison.Ordinal);
        Assert.Equal(PurchaseOrderStatus.Sent, (await _h.Sender.Send(new GetPurchaseOrderQuery(saved.Value)))!.Status);
        Assert.Equal(ErrorCodes.OrderNotEditable, (await _h.Sender.Send(new SavePurchaseOrderCommand(order))).Error.Code);

        var prefill = await _h.Sender.Send(new PrefillPurchaseFromOrderQuery(saved.Value));
        Assert.True(prefill.IsSuccess);
        var entry = prefill.Value with { SupplierBillNo = "RA-77", Lines = [prefill.Value.Lines[0] with { CostPrice = new Paise(4000) }] };
        var received = await _h.Sender.Send(new RecordPurchaseCommand(entry));
        Assert.True(received.IsSuccess, received.Error.Code);
        Assert.Equal(11_000, await _h.StockAsync(low.Id));
        var done = (await _h.Sender.Send(new GetPurchaseOrderQuery(saved.Value)))!;
        Assert.Equal(PurchaseOrderStatus.Received, done.Status);
        Assert.Equal(received.Value, done.PurchaseId);
        Assert.Equal(ErrorCodes.OrderNotEditable, (await _h.Sender.Send(new PrefillPurchaseFromOrderQuery(saved.Value))).Error.Code);

        var month = new DateOnly(_h.Clock.Today.Year, _h.Clock.Today.Month, 1);
        var bySupplier = Assert.Single(await _h.Sender.Send(new GetPurchasesBySupplierQuery(month, _h.Clock.Today)));
        Assert.Equal("Ravi Agencies", bySupplier.SupplierName);
        Assert.Equal(1, bySupplier.Bills);
        Assert.Equal(42_000, bySupplier.Total.Value);   // 10 × ₹40 × 1.05
    }

    [Fact]
    public async Task ValuationAndMarginEstimate_UseKnownCostsOnly()
    {
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync("36ABCDE1234F1Z5");   // GST shop: margin on sales before GST
        var supplier = await SupplierAsync();
        var a = await _h.AddItemAsync("Oil", 10_500, stockMilli: 0, unit: Unit.Piece);   // ₹105 incl. 5% = ₹100 before GST
        var noCost = await _h.AddItemAsync("Salt", 2000, stockMilli: 5000, unit: Unit.Piece);
        Assert.True((await BuyAsync(supplier, Line(a, 10_000, 8000))).IsSuccess);   // cost ₹80
        Assert.True((await SellAsync(a, 2000)).IsSuccess);
        Assert.True((await SellAsync(noCost, 1000)).IsSuccess);

        var value = await _h.Sender.Send(new GetStockValuationQuery());
        Assert.Equal(8 * 8000, value.AtCost.Value);
        Assert.Equal(1, value.ItemsValued);
        Assert.Equal(1, value.ItemsWithoutCost);

        var margin = await _h.Sender.Send(new GetMarginEstimateQuery(_h.Clock.Today, _h.Clock.Today));
        Assert.Equal(20_000, margin.SalesBeforeGst.Value);
        Assert.Equal(16_000, margin.Cost.Value);
        Assert.Equal(4_000, margin.Margin.Value);
        Assert.Equal(2000, margin.MarginBasisPoints);
        Assert.Equal(1, margin.LinesWithoutCost);
    }

    [Fact]
    public async Task ExpiringList_BucketsAndValuesAtCost()
    {
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync();
        var supplier = await SupplierAsync();
        var item = await TrackedAsync("Vitamin C");
        var t = _h.Clock.Today;
        Assert.True((await BuyAsync(supplier,
            Line(item, 1000, 1000, "E0", t.AddDays(5)),
            Line(item, 2000, 1000, "E45", t.AddDays(45)),
            Line(item, 3000, 1000, "E80", t.AddDays(80)),
            Line(item, 4000, 1000, "E200", t.AddDays(200)))).IsSuccess);
        _h.Clock.Advance(TimeSpan.FromDays(10));   // E0 expired; E45 now 35 days; E80 now 70 days

        var r = await _h.Sender.Send(new GetExpiringQuery());
        Assert.Equal(1, r.Expired.Count);
        Assert.Equal(1000, r.Expired.Value.Value);
        Assert.Equal(0, r.Within30.Count);
        Assert.Equal(1, r.Within60.Count);
        Assert.Equal(2, r.Within90.Count);
        Assert.Equal(5000, r.Within90.Value.Value);
        Assert.Equal(new[] { "E0", "E45", "E80" }, r.Lines.Select(l => l.BatchNo));
        Assert.True(r.HasAlerts);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(2026)]
    [InlineData(777)]
    public async Task RandomSequences_PurchaseSaleVoidDamageCorrection_KeepEveryInvariant(int seed)
    {
        var rng = new Random(seed);
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync(seed % 2 == 0 ? "36ABCDE1234F1Z5" : null);
        var supplier = await SupplierAsync();
        var tracked = new List<Item>();
        for (var i = 0; i < 3; i++) tracked.Add(await TrackedAsync("Med" + i, 1000 + 500 * i, openingMilli: i == 0 ? 4000 : 0));
        var plain = await _h.AddItemAsync("Plain", 2500, stockMilli: 10_000, unit: Unit.Piece);
        var all = tracked.Append(plain).ToList();
        var model = new Dictionary<Guid, long> { [tracked[0].Id] = 4000, [tracked[1].Id] = 0, [tracked[2].Id] = 0, [plain.Id] = 10_000 };
        var live = new List<(Guid BillId, Guid ItemId, long Milli)>();
        var purchases = new List<Guid>();
        string[] batchNos = ["B1", "B2", "B3"];

        for (var step = 0; step < 60; step++)
        {
            _h.Clock.Advance(TimeSpan.FromHours(rng.Next(1, 72)));
            var item = all[rng.Next(all.Count)];
            var op = rng.Next(0, 7);
            var label = $"seed {seed} step {step} op {op} {item.EnglishName}";
            switch (op)
            {
                case 0:
                case 1:   // purchase (some batches already expired, some re-used numbers)
                {
                    var milli = rng.Next(1, 8) * 1000;
                    var no = batchNos[rng.Next(batchNos.Length)];
                    var expiry = _h.Clock.Today.AddDays(rng.Next(-20, 300));
                    var r = await BuyAsync(supplier, item.TracksBatches ? Line(item, milli, 700, no, expiry) : Line(item, milli, 700));
                    Assert.True(r.IsSuccess, label + " " + r.Error.Code);
                    purchases.Add(r.Value);
                    model[item.Id] += milli;
                    break;
                }
                case 2:
                case 3:   // sale: refused only when it would reach expired stock unconfirmed
                {
                    var milli = rng.Next(1, 6) * 1000;
                    var confirm = rng.Next(0, 2) == 0;
                    var wouldTouchExpired = false;
                    if (item.TracksBatches)
                    {
                        var today = _h.Clock.Today;
                        var batches = await BatchesAsync(item);
                        var fresh = batches.Where(b => b.Qty.Milli > 0 && !b.IsExpired(today)).Sum(b => b.Qty.Milli);
                        var expired = batches.Where(b => b.Qty.Milli > 0 && b.IsExpired(today)).Sum(b => b.Qty.Milli);
                        wouldTouchExpired = milli > fresh && expired > 0;
                    }
                    var r = await SellAsync(item, milli, confirm);
                    if (wouldTouchExpired && !confirm)
                    {
                        Assert.Equal(ErrorCodes.BatchExpired, r.Error.Code);
                        break;
                    }
                    Assert.True(r.IsSuccess, label + " " + r.Error.Code);
                    live.Add((r.Value.BillId, item.Id, milli));
                    model[item.Id] -= milli;
                    break;
                }
                case 4:   // void a live bill
                {
                    if (live.Count == 0) break;
                    var bill = live[rng.Next(live.Count)];
                    var r = await _h.Sender.Send(new VoidBillCommand(bill.BillId, "random", ConfirmPastMonth: true));
                    Assert.True(r.IsSuccess, label + " " + r.Error.Code);
                    live.Remove(bill);
                    model[bill.ItemId] += bill.Milli;
                    break;
                }
                case 5:   // damage on a batch (tracked) or plain damage
                {
                    if (!item.TracksBatches)
                    {
                        Assert.True((await _h.Sender.Send(new AdjustStockCommand(item.Id, Qty.Whole(-1), StockMoveReason.Damage, null))).IsSuccess);
                        model[item.Id] -= 1000;
                        break;
                    }
                    var stocked = (await BatchesAsync(item)).Where(b => b.Qty.Milli >= 1000).ToList();
                    if (stocked.Count == 0) break;
                    var batch = stocked[rng.Next(stocked.Count)];
                    var r = await _h.Sender.Send(new AdjustBatchStockCommand(item.Id, batch.Id, Qty.Whole(-1), StockMoveReason.Damage));
                    Assert.True(r.IsSuccess, label + " " + r.Error.Code);
                    model[item.Id] -= 1000;
                    break;
                }
                default:  // purchase correction of 1 unit, when the batch / line still allows it
                {
                    if (purchases.Count == 0) break;
                    var p = (await _h.Sender.Send(new GetPurchaseQuery(purchases[rng.Next(purchases.Count)])))!;
                    var line = p.Lines[0];
                    var r = await _h.Sender.Send(new CorrectPurchaseLineCommand(p.Id, line.Id, Qty.Whole(1)));
                    if (r.IsSuccess) model[line.ItemId] -= 1000;
                    else Assert.Contains(r.Error.Code, new[] { ErrorCodes.BatchQtyTooLow, ErrorCodes.PurchaseCorrectionTooLarge });
                    break;
                }
            }
            foreach (var i in all) Assert.Equal(model[i.Id], await _h.StockAsync(i.Id));
            await AssertInvariantsAsync(label);
        }
    }
}

/// <summary>v2-stock: the restore sweep reconciles batches; a good backup with batches restores and verifies.</summary>
public sealed class StockRestoreTests
{
    private static async Task<(TempDb t, BackupService backup, Item item)> ReadyAsync()
    {
        var t = new TempDb();
        var item = new Item { TeluguName = "డోలో", EnglishName = "Dolo", Unit = Unit.Strip, Price = new Paise(3000), TracksBatches = true };
        await t.Items.SaveAsync(item);
        await t.Ledger.ReceiveAsync(item.Id, Qty.Whole(5), StockMoveReason.Purchase, null, "A", t.Clock.Today.AddMonths(3), new Paise(2000), null);
        await t.Ledger.ReceiveAsync(item.Id, Qty.Whole(4), StockMoveReason.Purchase, null, "B", t.Clock.Today.AddMonths(6), new Paise(2000), null);
        await t.Ledger.AdjustAsync(item.Id, Qty.Whole(-6), StockMoveReason.Sale);   // FEFO: 5 from A, 1 from B
        var backup = new BackupService(t.Db, t.Clock, Path.Combine(t.Folder, "exports"), TempDb.Log<BackupService>());
        return (t, backup, item);
    }

    [Fact]
    public async Task GoodBackupWithBatches_Restores_AndItsHmacVerifies()
    {
        var (t, backup, item) = await ReadyAsync();
        using var owner = t;
        var file = (await backup.ExportAsync()).Value;
        Assert.True((await backup.ReadMetadataAsync(file))!.HmacValid);
        t.Clock.Advance(TimeSpan.FromMinutes(5));
        var r = await backup.RestoreAsync(file);
        Assert.True(r.IsSuccess, r.Error.Detail);
        Assert.Equal(3000, (await t.Items.GetAsync(item.Id))!.Stock.Milli);
        Assert.Equal(3000, (await t.Ledger.GetBatchesAsync(item.Id)).Sum(b => b.Qty.Milli));
    }

    [Fact]
    public async Task BackupWhoseBatchesDoNotReconcile_IsRejected_LiveDataUntouched()
    {
        var (t, backup, item) = await ReadyAsync();
        using var owner = t;
        // Move a strip from batch A to batch B by hand: Item.Stock and Σ moves still agree, the batches do not.
        await t.Db.RunAsync(c =>
        {
            c.Execute("UPDATE Batch SET QtyMilli = QtyMilli + 1000 WHERE BatchNo = 'A'");
            c.Execute("UPDATE Batch SET QtyMilli = QtyMilli - 1000 WHERE BatchNo = 'B'");
        });
        var tampered = (await backup.ExportAsync()).Value;
        await t.Db.RunAsync(c =>
        {
            c.Execute("UPDATE Batch SET QtyMilli = QtyMilli - 1000 WHERE BatchNo = 'A'");
            c.Execute("UPDATE Batch SET QtyMilli = QtyMilli + 1000 WHERE BatchNo = 'B'");
        });
        t.Clock.Advance(TimeSpan.FromMinutes(5));
        var r = await backup.RestoreAsync(tampered);
        Assert.Equal(ErrorCodes.RestoreInvalidFile, r.Error.Code);
        Assert.Contains("batch", r.Error.Detail, StringComparison.Ordinal);
        Assert.Equal(0, (await t.Ledger.GetBatchesAsync(item.Id)).Single(b => b.BatchNo == "A").Qty.Milli);

        // Σ batches != stock (a batch row with no moves at all) is caught too.
        await t.Db.RunAsync(c => c.Execute("UPDATE Batch SET QtyMilli = QtyMilli + 1000 WHERE BatchNo = 'B'"));
        var ok = await t.Db.RunAsync(c => BackupService.BatchesReconcile(c, out string _));
        Assert.False(ok);
    }
}

public sealed class ExpiryParsingTests
{
    [Theory]
    [InlineData("12/27", 2027, 12, 31)]
    [InlineData("1227", 2027, 12, 31)]      // Android number keyboard: no '/'
    [InlineData("02/2028", 2028, 2, 29)]
    [InlineData("15-06-2027", 2027, 6, 15)]
    public void ParseExpiry_ReadsPrintedFormats(string text, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), BatchAllocator.ParseExpiry(text));

    [Theory]
    [InlineData("")]
    [InlineData("13/27")]
    [InlineData("1327")]
    [InlineData("abc")]
    [InlineData("31/02/2027")]
    public void ParseExpiry_RejectsNonsense(string text) => Assert.Null(BatchAllocator.ParseExpiry(text));
}
