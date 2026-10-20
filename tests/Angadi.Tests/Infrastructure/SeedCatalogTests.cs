using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Inventory;
using Angadi.Domain.Billing;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Seed;
using Angadi.Tests.Application;

namespace Angadi.Tests.Infrastructure;

/// <summary>
/// T2: the first-run sample catalogs (FR-04). Every seeded item must be one a shopkeeper can bill today: a current
/// GST 2.0 slab, a positive price, Telugu and English names, sane stock numbers, no barcode clashes.
/// </summary>
public sealed class SeedCatalogTests
{
    public static TheoryData<string> Catalogs => new() { "kirana", "pharmacy" };

    private static IReadOnlyList<SeedCatalog.SeedItem> Load(string name) =>
        name == "kirana" ? SeedCatalog.Kirana() : SeedCatalog.Pharmacy();

    private static bool HasTelugu(string s) => s.Any(c => c is >= 'ఀ' and <= '౿');

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void EverySeededItem_IsValidAndBillableToday(string catalog)
    {
        var rows = Load(catalog);
        Assert.NotEmpty(rows);
        var validator = new SaveItemValidator();

        foreach (var (item, opening) in rows)
        {
            var who = $"{catalog}: {item.EnglishName}";
            Assert.True(HasTelugu(item.TeluguName), who + " has no Telugu name");
            Assert.False(string.IsNullOrWhiteSpace(item.EnglishName), who + " has no English name");
            Assert.False(HasTelugu(item.EnglishName), who + " English name is in Telugu");
            Assert.Equal(item.TeluguName.Trim(), item.TeluguName);
            Assert.Equal(item.EnglishName.Trim(), item.EnglishName);
            Assert.False(string.IsNullOrWhiteSpace(item.Category), who + " has no category");
            Assert.Contains(item.GstSlab, GstSlabs.Current);
            Assert.True(item.Price.Value > 0, who + " has no price");
            Assert.True(item.Price.Value <= SaveItemValidator.MaxPricePaise, who + " price too high");
            Assert.True(Enum.IsDefined(item.Unit), who + " unknown unit");
            Assert.True(opening.Milli >= 0, who + " negative opening stock");
            Assert.True(item.LowStockThreshold.Milli >= 0, who + " negative threshold");
            Assert.Equal(0, item.Stock.Milli);   // stock only ever arrives through the ledger
            Assert.True(item.IsActive);
            Assert.All(item.Aliases, a => Assert.False(string.IsNullOrWhiteSpace(a), who + " blank alias"));
            if (item.Barcode is { } code)
            {
                Assert.Equal(code, Barcodes.Normalize(code));
                Assert.True(Barcodes.IsValid(code), who + " bad barcode check digit");
            }
            Assert.Null(validator.Validate(new SaveItemCommand(item, opening)));
        }
    }

    [Fact]
    public void BothCatalogsTogether_HaveUniqueNamesAndBarcodes()
    {
        var all = SeedCatalog.Kirana().Concat(SeedCatalog.Pharmacy()).Select(s => s.Item).ToList();
        Assert.Equal(all.Count, all.Select(i => i.TeluguName).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Count, all.Select(i => i.EnglishName.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Count, all.Select(i => i.Id).Distinct().Count());
        var codes = all.Where(i => i.Barcode is not null).Select(i => Barcodes.Normalize(i.Barcode)!).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        var alternates = codes.Select(Barcodes.Alternate).Where(a => a is not null).ToList();
        Assert.DoesNotContain(alternates, a => codes.Contains(a!, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true, false, 80)]
    [InlineData(false, true, 40)]
    [InlineData(true, true, 120)]
    public async Task Seeding_GoesThroughTheLedger_AndTheInventoryReportAddsUp(bool kirana, bool pharmacy, int count)
    {
        using var h = new AppHost();
        var seeded = await h.Sender.Send(new SeedItemsCommand(kirana, pharmacy));
        Assert.True(seeded.IsSuccess);
        Assert.Equal(count, seeded.Value);

        var expected = (kirana ? SeedCatalog.Kirana() : []).Concat(pharmacy ? SeedCatalog.Pharmacy() : [])
            .ToDictionary(s => s.Item.EnglishName, StringComparer.Ordinal);
        var r = await h.Sender.Send(new GetInventoryReportQuery());
        Assert.True(r.LedgerMatches);
        Assert.Equal(count, r.ItemCount);
        Assert.Equal(expected.Values.Sum(s => (2 * s.Item.Price.Value * s.OpeningStock.Milli + 1000) / 2000), r.StockValue.Value);
        Assert.Equal(expected.Values.Count(s => s.OpeningStock.Milli <= 0), r.OutCount);
        Assert.Equal(expected.Values.Count(s => s.OpeningStock.Milli > 0 && s.OpeningStock <= s.Item.LowStockThreshold), r.LowCount);

        foreach (var item in await h.Sender.Send(new GetAllItemsQuery()))
        {
            var seed = expected[item.EnglishName];
            Assert.Equal(seed.OpeningStock, item.Stock);
            var moves = await h.Sender.Send(new GetStockHistoryQuery(item.Id));
            if (seed.OpeningStock.Milli > 0)
                Assert.Equal(StockMoveReason.Opening, Assert.Single(moves).Reason);
            else
                Assert.Empty(moves);
        }
    }

    [Fact]
    public async Task EverySeededItem_CanBeBilled_OnATaxInvoice()
    {
        using var h = new AppHost();
        h.Subscription.IsPro = true;
        await h.SeedShopAsync("36ABCDE1234F1Z5");
        Assert.True((await h.Sender.Send(new SeedItemsCommand(true, true))).IsSuccess);
        var items = await h.Sender.Send(new GetAllItemsQuery());

        var bill = AppHost.Draft(TenderType.Cash, items.Select(i => (i, 1_000L)).ToArray());
        var r = await h.Sender.Send(new FinalizeBillCommand(bill));
        Assert.True(r.IsSuccess, r.Error.Code);
        Assert.True((await h.Sender.Send(new GetInventoryReportQuery())).LedgerMatches);
    }
}
