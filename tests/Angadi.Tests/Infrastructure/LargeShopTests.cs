using System.Diagnostics;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Persistence;

namespace Angadi.Tests.Infrastructure;

/// <summary>
/// PERF-2: the counter must stay quick for a big shop (50,000 items, 20,000 customers). The limits are generous
/// (CI machines are slow); they catch a scan that has gone quadratic, not small drifts.
/// </summary>
public sealed class LargeShopTests : IDisposable
{
    private const int ItemCount = 50_000;
    private const int CustomerCount = 20_000;
    private readonly TempDb _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task ItemAndCustomerSearch_StayQuick_OnABigShop()
    {
        await _t.Db.ExecuteAsync(async () =>
        {
            for (var i = 0; i < ItemCount; i++)
                await _t.Items.SaveAsync(new Item
                {
                    TeluguName = $"సరుకు {i}", EnglishName = $"Item {i}", Barcode = $"89{i:D11}", Price = new Paise(1000 + i % 500),
                    Aliases = [$"alias{i}"], Stock = Qty.Whole(10),
                });
            for (var i = 0; i < CustomerCount; i++)
                await _t.Customers.SaveAsync(new Customer { Name = $"Customer {i}", Phone = $"9{i:D9}" });
            return true;
        }, ok => ok);

        var sw = Stopwatch.StartNew();
        var byName = await _t.Items.SearchAsync("Item 4999");
        var nameTime = sw.Elapsed;
        sw.Restart();
        var byBarcode = await _t.Items.GetByBarcodeAsync("89" + 25_000.ToString("D11"));
        var barcodeTime = sw.Elapsed;
        sw.Restart();
        var customers = await _t.Customers.SearchAsync("Customer 1999");
        var customerTime = sw.Elapsed;
        sw.Restart();
        var low = await _t.Items.GetLowStockAsync();
        var lowTime = sw.Elapsed;

        Assert.NotEmpty(byName);
        Assert.NotNull(byBarcode);
        Assert.NotEmpty(customers);
        Assert.Equal(ItemCount, low.Count);   // saved straight to the table, every item has stock 0: the worst case for this list
        Assert.True(nameTime < TimeSpan.FromSeconds(1.5), $"item search took {nameTime}");
        Assert.True(barcodeTime < TimeSpan.FromMilliseconds(300), $"barcode lookup took {barcodeTime}");
        Assert.True(customerTime < TimeSpan.FromSeconds(1.5), $"customer search took {customerTime}");
        Assert.True(lowTime < TimeSpan.FromSeconds(5), $"low-stock list of {ItemCount} items took {lowTime}");
    }
}
