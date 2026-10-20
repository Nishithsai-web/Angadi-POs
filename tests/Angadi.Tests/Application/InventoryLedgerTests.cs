using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Inventory;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;

namespace Angadi.Tests.Application;

/// <summary>
/// T2: stock must always tell the shopkeeper the truth. Seeded random sequences of every kind of stock change
/// (new item with opening stock, purchase, sale, void, damage, count correction, item edit, deactivate), checked
/// after EVERY step against an independent model: Item.Stock = Σ its StockMoves, the inventory report's ledger
/// check, stock value, low/out counts. Negative stock (sold before stock-in) is allowed but must be reported as out.
/// </summary>
public sealed class InventoryLedgerTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private sealed class Tracked(Item item, long stock)
    {
        public Item Item { get; } = item;   // the caller's own (possibly stale) object, as a page would hold it
        public long Stock { get; set; } = stock;
        public bool Active { get; set; } = true;
    }

    private sealed record LiveBill(Guid Id, List<(Guid ItemId, long Milli)> Lines);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(20261019)]
    public async Task RandomSequences_StockLedgerAndInventoryReport_StayTrueAfterEveryStep(int seed)
    {
        var rng = new Random(seed);
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync(seed % 2 == 0 ? "36ABCDE1234F1Z5" : null);
        var customer = (await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "రవి" }))).Value;

        var model = new Dictionary<Guid, Tracked>();
        var live = new List<LiveBill>();
        var firstDay = _h.Clock.Today;

        for (var step = 0; step < 70; step++)
        {
            _h.Clock.Advance(TimeSpan.FromMinutes(rng.Next(1, 300)));   // crosses several days per run
            var active = model.Values.Where(t => t.Active).ToList();
            var op = active.Count < 2 ? 0 : rng.Next(0, 10);
            string label;

            switch (op)
            {
                case 0:   // new item, with or without opening stock
                {
                    var opening = rng.Next(0, 3) == 0 ? 0 : rng.Next(1, 20_000);
                    var item = new Item
                    {
                        TeluguName = $"సరుకు {step}", EnglishName = $"Item {step}",
                        Unit = (Unit)rng.Next(0, 7), Price = new Paise(rng.Next(100, 50_000)),
                        GstSlab = GstSlabs.Current[rng.Next(GstSlabs.Current.Count)],
                        LowStockThreshold = new Qty(rng.Next(0, 5_000)),
                    };
                    var r = await _h.Sender.Send(new SaveItemCommand(item, opening == 0 ? null : new Qty(opening)));
                    Assert.True(r.IsSuccess, r.Error.Code);
                    model[item.Id] = new Tracked(item, opening);
                    label = $"new item opening {opening}";
                    break;
                }
                case 1:   // purchase / stock-in
                {
                    var t = Pick(rng, active);
                    var qty = rng.Next(1, 10_000);
                    Assert.True((await _h.Sender.Send(new AdjustStockCommand(t.Item.Id, new Qty(qty), StockMoveReason.Purchase, "stock-in"))).IsSuccess);
                    t.Stock += qty;
                    label = $"purchase {qty}";
                    break;
                }
                case 2:
                case 3:   // sale: often more than is on the shelf, so stock goes negative
                {
                    var lines = Enumerable.Range(0, rng.Next(1, 4))
                        .Select(_ => (Pick(rng, active), (long)rng.Next(1, 6_000))).ToList();
                    var tender = (TenderType)rng.Next(0, 3);
                    var draft = AppHost.Draft(tender, lines.Select(l => (l.Item1.Item, l.Item2)).ToArray());
                    if (tender == TenderType.Udhaar) draft.CustomerId = customer;
                    var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
                    Assert.True(r.IsSuccess, r.Error.Code);
                    foreach (var (t, milli) in lines) t.Stock -= milli;
                    live.Add(new LiveBill(r.Value.BillId, lines.Select(l => (l.Item1.Item.Id, l.Item2)).ToList()));
                    label = $"sale {tender} {lines.Count} lines";
                    break;
                }
                case 4:   // void a bill (any day, any item — even a deactivated one gets its stock back)
                {
                    if (live.Count == 0) goto case 1;
                    var bill = live[rng.Next(live.Count)];
                    var r = await _h.Sender.Send(new VoidBillCommand(bill.Id, "wrong item"));
                    Assert.True(r.IsSuccess, r.Error.Code);
                    foreach (var (itemId, milli) in bill.Lines) model[itemId].Stock += milli;
                    live.Remove(bill);
                    label = "void";
                    break;
                }
                case 5:   // damage / breakage
                {
                    var t = Pick(rng, active);
                    var qty = rng.Next(1, 2_000);
                    Assert.True((await _h.Sender.Send(new AdjustStockCommand(t.Item.Id, new Qty(-qty), StockMoveReason.Damage, "broken"))).IsSuccess);
                    t.Stock -= qty;
                    label = $"damage {qty}";
                    break;
                }
                case 6:   // shelf count: the owner counts N, the correction is N − stock
                {
                    var t = Pick(rng, active);
                    var counted = rng.Next(0, 20_000);
                    var delta = counted - t.Stock;
                    if (delta == 0) goto case 1;
                    Assert.True((await _h.Sender.Send(new AdjustStockCommand(t.Item.Id, new Qty(delta), StockMoveReason.CountCorrection, null))).IsSuccess);
                    t.Stock = counted;
                    label = $"count to {counted}";
                    break;
                }
                case 7:
                case 8:   // item edit from a STALE object; an opening stock on an edit must be ignored
                {
                    var t = Pick(rng, active);
                    t.Item.Price = new Paise(rng.Next(100, 50_000));
                    t.Item.LowStockThreshold = new Qty(rng.Next(0, 8_000));
                    t.Item.EnglishName = $"Item {step} edited";
                    var r = await _h.Sender.Send(new SaveItemCommand(t.Item, rng.Next(0, 2) == 0 ? null : new Qty(rng.Next(1, 9_000))));
                    Assert.True(r.IsSuccess, r.Error.Code);
                    label = "edit";
                    break;
                }
                default:   // deactivate
                {
                    if (active.Count <= 3) goto case 1;
                    var t = Pick(rng, active);
                    t.Item.IsActive = false;
                    Assert.True((await _h.Sender.Send(new SaveItemCommand(t.Item))).IsSuccess);
                    t.Active = false;
                    label = "deactivate";
                    break;
                }
            }

            await AssertTruthAsync(model, $"seed {seed} step {step} ({label})");
        }

        // The stock-moves list over the whole run explains every unit of every item's stock.
        var moves = await _h.Sender.Send(new GetStockMovesQuery(firstDay, _h.Clock.Today));
        foreach (var t in model.Values)
            Assert.Equal((t.Item.EnglishName, t.Stock), (t.Item.EnglishName, moves.Where(m => m.ItemId == t.Item.Id).Sum(m => m.Delta.Milli)));
    }

    private static Tracked Pick(Random rng, List<Tracked> from) => from[rng.Next(from.Count)];

    private async Task AssertTruthAsync(Dictionary<Guid, Tracked> model, string step)
    {
        foreach (var t in model.Values)
        {
            var stored = await _h.Sender.Send(new GetItemQuery(t.Item.Id));
            Assert.NotNull(stored);
            Assert.Equal((step, t.Item.Id, t.Stock), (step, t.Item.Id, stored.Stock.Milli));
            Assert.Equal((step, t.Active), (step, stored.IsActive));
            var history = await _h.Sender.Send(new GetStockHistoryQuery(t.Item.Id));
            Assert.Equal((step, "ledger", t.Stock), (step, "ledger", history.Sum(m => m.Delta.Milli)));
        }

        var r = await _h.Sender.Send(new GetInventoryReportQuery());
        Assert.True(r.LedgerMatches, step + ": " + string.Join(", ", r.Mismatches.Select(m => $"{m.Name} {m.Stock.Milli}≠{m.Ledger.Milli}")));

        var active = model.Values.Where(t => t.Active).ToList();
        var expectedValue = active.Sum(t => t.Stock <= 0 ? 0 : (2 * t.Item.Price.Value * t.Stock + 1000) / 2000);
        Assert.Equal((step, "items", active.Count), (step, "items", r.ItemCount));
        Assert.Equal((step, "out", active.Count(t => t.Stock <= 0)), (step, "out", r.OutCount));
        Assert.Equal((step, "low", active.Count(t => t.Stock > 0 && t.Stock <= t.Item.LowStockThreshold.Milli)), (step, "low", r.LowCount));
        Assert.Equal((step, "value", expectedValue), (step, "value", r.StockValue.Value));
        Assert.Equal((step, "lines", active.Count), (step, "lines", r.Lines.Count));

        foreach (var line in r.Lines)
        {
            Assert.True(model.TryGetValue(line.ItemId, out var t) && t.Active, $"{step}: inactive or unknown item on the report");
            Assert.Equal((step, line.ItemId, t!.Stock, t.Stock <= 0), (step, line.ItemId, line.Stock.Milli, line.IsOut));
            Assert.True(line.Value.Value >= 0, $"{step}: negative stock value");
        }
        // Out-of-stock (including negative) lines lead the list, so a shopkeeper sees them first.
        var firstInStock = r.Lines.ToList().FindIndex(l => !l.IsOut);
        if (firstInStock >= 0) Assert.DoesNotContain(r.Lines.Skip(firstInStock), l => l.IsOut);

        // The low-stock alert list agrees with the report: every low and every out item, nothing else.
        var alert = await _h.Sender.Send(new GetLowStockQuery());
        Assert.Equal((step, "alerts", r.LowCount + r.OutCount), (step, "alerts", alert.Count));
    }

    // ---------- Targeted cases ----------

    [Fact]
    public async Task NegativeStock_SoldBeforeStockIn_IsReportedOut_ValuedAtZero_AndLedgerStillMatches()
    {
        _h.Subscription.IsPro = true;
        await _h.SeedShopAsync();
        var milk = await _h.AddItemAsync("పాలు", 3000, stockMilli: 2_000, unit: Unit.Pack);
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (milk, 5_000))))).IsSuccess);

        var r = await _h.Sender.Send(new GetInventoryReportQuery());
        var line = Assert.Single(r.Lines);
        Assert.Equal(-3_000, line.Stock.Milli);
        Assert.True(line.IsOut);
        Assert.Equal(0, line.Value.Value);
        Assert.Equal(0, r.StockValue.Value);
        Assert.True(r.LedgerMatches);
        Assert.Contains(await _h.Sender.Send(new GetLowStockQuery()), i => i.Id == milk.Id);

        // The late stock-in brings it back to the truth.
        await _h.Sender.Send(new AdjustStockCommand(milk.Id, Qty.Whole(10), StockMoveReason.Purchase, null));
        r = await _h.Sender.Send(new GetInventoryReportQuery());
        Assert.Equal(7_000, Assert.Single(r.Lines).Stock.Milli);
        Assert.Equal(21_000, r.StockValue.Value);
        Assert.True(r.LedgerMatches);
    }

    [Fact]
    public async Task StockValue_RoundsHalfUpPerLine_ForFractionalQuantities()
    {
        // 0.333 kg × ₹10.01 = ₹3.33333 → 333 paise; 0.5 kg × ₹0.03 = 1.5 paise → 2 paise
        var a = await _h.AddItemAsync("a", 1001, stockMilli: 333);
        var b = await _h.AddItemAsync("b", 3, stockMilli: 500);
        var r = await _h.Sender.Send(new GetInventoryReportQuery());
        Assert.Equal(333, r.Lines.Single(l => l.ItemId == a.Id).Value.Value);
        Assert.Equal(2, r.Lines.Single(l => l.ItemId == b.Id).Value.Value);
        Assert.Equal(335, r.StockValue.Value);
    }

    [Fact]
    public async Task DeactivatedItem_LeavesTheReport_ButItsLedgerStaysWhole()
    {
        var soap = await _h.AddItemAsync("సబ్బు", 4000, stockMilli: 5_000, unit: Unit.Piece);
        await _h.AddItemAsync("ఉప్పు", 2800, stockMilli: 3_000, unit: Unit.Pack);
        soap.IsActive = false;
        Assert.True((await _h.Sender.Send(new SaveItemCommand(soap))).IsSuccess);

        var r = await _h.Sender.Send(new GetInventoryReportQuery());
        Assert.DoesNotContain(r.Lines, l => l.ItemId == soap.Id);
        Assert.Equal(8_400, r.StockValue.Value);
        Assert.True(r.LedgerMatches);
        Assert.Equal(5_000, (await _h.Sender.Send(new GetStockHistoryQuery(soap.Id))).Sum(m => m.Delta.Milli));
        Assert.DoesNotContain(await _h.Sender.Send(new GetLowStockQuery()), i => i.Id == soap.Id);
    }

    [Fact]
    public async Task ZeroThreshold_ZeroStock_IsOutNotLow_AndThresholdEqualStock_IsLow()
    {
        var empty = await _h.AddItemAsync("empty", 1000, stockMilli: 0);
        var edge = await _h.AddItemAsync("edge", 1000, stockMilli: 2_000);
        edge.LowStockThreshold = new Qty(2_000);
        await _h.Sender.Send(new SaveItemCommand(edge));

        var r = await _h.Sender.Send(new GetInventoryReportQuery());
        Assert.Equal(1, r.OutCount);
        Assert.Equal(1, r.LowCount);
        Assert.True(r.Lines.Single(l => l.ItemId == empty.Id).IsOut);
        Assert.True(r.Lines.Single(l => l.ItemId == edge.Id).IsLow);
        Assert.False(r.Lines.Single(l => l.ItemId == edge.Id).IsOut);
    }

    [Fact]
    public async Task AdjustStock_RejectsZero_SaleReasons_AndUnknownItems_WithoutTouchingTheLedger()
    {
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 10_000);
        Assert.Equal(ErrorCodes.BillQtyInvalid, (await _h.Sender.Send(new AdjustStockCommand(rice.Id, Qty.Zero, StockMoveReason.Purchase, null))).Error.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, (await _h.Sender.Send(new AdjustStockCommand(rice.Id, new Qty(-1000), StockMoveReason.Sale, null))).Error.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, (await _h.Sender.Send(new AdjustStockCommand(rice.Id, new Qty(1000), StockMoveReason.VoidRestore, null))).Error.Code);
        Assert.Equal(ErrorCodes.ItemNotFound, (await _h.Sender.Send(new AdjustStockCommand(Guid.NewGuid(), new Qty(1000), StockMoveReason.Purchase, null))).Error.Code);

        Assert.Equal(10_000, await _h.StockAsync(rice.Id));
        Assert.Single(await _h.Sender.Send(new GetStockHistoryQuery(rice.Id)));
        Assert.True((await _h.Sender.Send(new GetInventoryReportQuery())).LedgerMatches);
    }

    [Fact]
    public async Task NewItem_WithStockPreset_NeverBypassesTheLedger()
    {
        // e.g. a page that copies an existing item ("duplicate this item") or re-saves a restored object as new.
        var copy = new Item { TeluguName = "కాపీ", EnglishName = "Copy", Unit = Unit.Piece, Price = new Paise(1000), Stock = Qty.Whole(5) };
        Assert.True((await _h.Sender.Send(new SaveItemCommand(copy, Qty.Whole(3)))).IsSuccess);

        var r = await _h.Sender.Send(new GetInventoryReportQuery());
        Assert.True(r.LedgerMatches);
        Assert.Equal(3_000, await _h.StockAsync(copy.Id));   // only the declared opening stock, through the ledger
    }
}
