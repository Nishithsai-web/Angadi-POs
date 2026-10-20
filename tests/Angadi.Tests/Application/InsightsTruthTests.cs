using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Insights;
using Angadi.Application.Inventory;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>
/// T2: Tier-1 insights (reorder forecast, dead stock, morning summary) on an empty shop, a shop with one day of
/// data, and a month of data: sensible numbers, never an exception, never a divide by zero.
/// </summary>
public sealed class InsightsTruthTests : IDisposable
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private readonly AppHost _h = new();
    private IInsightsService Insights => _h.Services.GetRequiredService<IInsightsService>();

    public InsightsTruthTests() => _h.Subscription.IsPro = true;
    public void Dispose() => _h.Dispose();

    private static DateTimeOffset At(int month, int day, int hour, int minute = 0, int second = 0) =>
        new(2026, month, day, hour, minute, second, Ist);

    private static async Task<FinalizedBill> SellAsync(AppHost h, TenderType tender, Guid? customer, params (Item item, long qtyMilli)[] lines)
    {
        var draft = AppHost.Draft(tender, lines);
        draft.CustomerId = customer;
        var r = await h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.Error.Code);
        return r.Value;
    }

    // ---------- Empty shop ----------

    [Fact]
    public async Task EmptyShop_EverythingIsEmptyOrZero_NothingThrows()
    {
        Assert.Empty(await Insights.GetReorderForecastAsync());
        Assert.Empty(await Insights.GetReorderForecastAsync(0));
        foreach (var days in new[] { 0, 1, 30, 60, 90, 365 })
            Assert.Empty(await Insights.GetDeadStockAsync(days));
        Assert.Equal(new MorningSummary(Paise.Zero, Paise.Zero, null, Paise.Zero, 0), await Insights.GetMorningSummaryAsync());
    }

    [Fact]
    public async Task ZeroOrNegativeWindowsInSettings_DoNotDivideByZero()
    {
        using var h = new AppHost(configure: s => s.Configure<InsightsOptions>(o => { o.ForecastWindowDays = 0; o.MorningSummaryDays = -3; }));
        h.Subscription.IsPro = true;
        await h.SeedShopAsync();
        var rice = await h.AddItemAsync("బియ్యం", 6_000, stockMilli: 3_000);
        h.Clock.Now = h.Clock.Now.AddDays(-1);
        await SellAsync(h, TenderType.Cash, null, (rice, 1_000));
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await SellAsync(h, TenderType.Cash, null, (rice, 1_000));

        var insights = h.Services.GetRequiredService<IInsightsService>();
        var f = Assert.Single(await insights.GetReorderForecastAsync());   // window clamps to 1 day: 1 kg/day, 1 kg left
        Assert.Equal(1, f.DaysLeft);
        Assert.Equal(1_000, f.AvgDailySales.Milli);
        var m = await insights.GetMorningSummaryAsync();
        Assert.Equal(6_000, m.YesterdaySales.Value);
        Assert.True(m.SevenDayAverage.Value >= 0);
    }

    // ---------- One day of data ----------

    [Fact]
    public async Task OneDayOfData_NoThrow_NewItemsAreNotDeadStock_YesterdayIsZero()
    {
        await _h.SeedShopAsync();
        _h.Clock.Now = At(10, 19, 8);
        var rice = await _h.AddItemAsync("బియ్యం", 6_000, stockMilli: 10_000);
        var soap = await _h.AddItemAsync("సబ్బు", 4_000, stockMilli: 5_000, unit: Unit.Piece);
        _h.Clock.Now = At(10, 19, 10);
        await SellAsync(_h, TenderType.Cash, null, (rice, 9_500));

        var forecast = await Insights.GetReorderForecastAsync();
        Assert.All(forecast, f => Assert.True(f.DaysLeft >= 0));
        Assert.DoesNotContain(forecast, f => f.ItemId == soap.Id);   // never sold: no rate, no forecast

        foreach (var days in new[] { 1, 30, 60, 90 })
            Assert.Empty(await Insights.GetDeadStockAsync(days));   // both added today

        var m = await Insights.GetMorningSummaryAsync();
        Assert.Equal(Paise.Zero, m.YesterdaySales);
        Assert.Equal(Paise.Zero, m.SevenDayAverage);
        Assert.Null(m.TopMoverName);
    }

    [Fact(Skip = "BUG T2-02: reorder forecast divides by the full 14-day window even when the shop has 1 day of sales, so a new shop is told it has 14x the days it really has")]
    public async Task OneDayOfData_FastSeller_IsForecastToRunOutSoon()
    {
        await _h.SeedShopAsync();
        _h.Clock.Now = At(10, 19, 8);
        var rice = await _h.AddItemAsync("బియ్యం", 6_000, stockMilli: 10_000);
        _h.Clock.Now = At(10, 19, 18);
        await SellAsync(_h, TenderType.Cash, null, (rice, 5_000));   // 5 kg sold on the first day, 5 kg left

        var f = Assert.Single(await Insights.GetReorderForecastAsync());
        Assert.Equal(rice.Id, f.ItemId);
        Assert.True(f.DaysLeft <= 1, $"5 kg left at 5 kg/day, forecast said {f.DaysLeft} days");
    }

    // ---------- A month of data ----------

    [Theory]
    [InlineData(30)]
    [InlineData(20)]
    [InlineData(60)]
    public async Task MonthOfData_ForecastDeadStockAndMorningSummary_AreTheLedgersTruth(int deadDays)
    {
        await _h.SeedShopAsync();
        var ravi = (await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "రవి" }))).Value;
        _h.Clock.Now = At(9, 19, 9);
        var rice = await _h.AddItemAsync("బియ్యం", 6_000, stockMilli: 35_000);                       // 1 kg/day → 5 kg left
        var dal = await _h.AddItemAsync("పప్పు", 12_000, stockMilli: 100_000);                       // 1 kg/day → 70 kg left
        var oil = await _h.AddItemAsync("నూనె", 20_000, stockMilli: 10_000, unit: Unit.Piece);      // sold once, 25 Sep
        var soap = await _h.AddItemAsync("సబ్బు", 4_000, stockMilli: 20_000, unit: Unit.Piece);     // never sold
        await _h.AddItemAsync("ఉప్పు", 2_800, stockMilli: 0, unit: Unit.Pack);                        // never stocked
        var sugar = await _h.AddItemAsync("చక్కెర", 4_500, stockMilli: 2_000);                       // oversold on 18 Oct

        var salesByDay = new Dictionary<DateOnly, long>();
        void Book(FinalizedBill b) => salesByDay[_h.Clock.Today] = salesByDay.GetValueOrDefault(_h.Clock.Today) + b.GrandTotal.Value;
        for (var d = 0; d < 30; d++)                                                                  // 19 Sep … 18 Oct
        {
            _h.Clock.Now = At(9, 19, 10).AddDays(d);
            Book(await SellAsync(_h, TenderType.Cash, null, (rice, 1_000), (dal, 1_000)));
            if (_h.Clock.Today == new DateOnly(2026, 9, 25)) Book(await SellAsync(_h, TenderType.Upi, null, (oil, 3_000)));
        }
        _h.Clock.Now = At(10, 18, 20);
        var udhaar = await SellAsync(_h, TenderType.Udhaar, ravi, (sugar, 5_000));
        Book(udhaar);
        _h.Clock.Now = At(10, 19, 7);   // the next morning

        // Reorder: oversold sugar first (0 days), then rice (5 kg ÷ 13 kg/14 days = 5 days); dal (75 days) and
        // oil (no sale in the window) are not listed.
        var forecast = await Insights.GetReorderForecastAsync();
        Assert.Equal([sugar.Id, rice.Id], forecast.Select(f => f.ItemId));
        Assert.Equal(0, forecast[0].DaysLeft);
        Assert.Equal(-3_000, forecast[0].Stock.Milli);
        Assert.Equal(5, forecast[1].DaysLeft);
        Assert.Equal(929, forecast[1].AvgDailySales.Milli);   // 13,000 ÷ 14, half up
        Assert.Contains(await Insights.GetReorderForecastAsync(100), f => f.ItemId == dal.Id);

        // Dead stock: only items WITH stock and no sale since the cutoff (or, never sold, added before it).
        var dead = await Insights.GetDeadStockAsync(deadDays);
        var cutoff = _h.Clock.Today.AddDays(-deadDays);
        var expected = new List<(Guid Id, DateOnly? LastSold, long Value)>();
        if (new DateOnly(2026, 9, 25) <= cutoff) expected.Add((oil.Id, new DateOnly(2026, 9, 25), 7 * 20_000));
        if (new DateOnly(2026, 9, 19) <= cutoff) expected.Add((soap.Id, null, 20 * 4_000));
        Assert.Equal(expected.OrderByDescending(e => e.Value), dead.Select(x => (x.ItemId, x.LastSold, x.StockValue.Value)));

        // Morning card: yesterday (18 Oct) and the 7 days before today (12–18 Oct), straight from the bills.
        var m = await Insights.GetMorningSummaryAsync();
        var yesterday = new DateOnly(2026, 10, 18);
        Assert.Equal(salesByDay[yesterday], m.YesterdaySales.Value);
        var week = Enumerable.Range(1, 7).Sum(i => salesByDay.GetValueOrDefault(_h.Clock.Today.AddDays(-i)));
        Assert.Equal(week / 7, m.SevenDayAverage.Value);
        Assert.Equal("చక్కెర", m.TopMoverName);                    // ₹225 of sugar beat ₹60 rice and ₹120 dal
        Assert.Equal(udhaar.GrandTotal, m.DuesToCollect);
        var inventory = await _h.Sender.Send(new GetInventoryReportQuery());
        Assert.Equal(inventory.LowCount + inventory.OutCount, m.LowStockCount);
        Assert.Equal(2, m.LowStockCount);                          // salt (0) and sugar (−3 kg)
        Assert.True(inventory.LedgerMatches);
    }

    [Fact]
    public async Task VoidedSales_DoNotCountAsSales_ForForecastDeadStockOrMorning()
    {
        await _h.SeedShopAsync();
        _h.Clock.Now = At(8, 1, 9);
        var oil = await _h.AddItemAsync("నూనె", 20_000, stockMilli: 10_000, unit: Unit.Piece);
        _h.Clock.Now = At(10, 18, 11);
        var bill = await SellAsync(_h, TenderType.Cash, null, (oil, 9_000));
        Assert.True((await _h.Sender.Send(new VoidBillCommand(bill.BillId, "wrong item"))).IsSuccess);
        _h.Clock.Now = At(10, 19, 7);

        Assert.Empty(await Insights.GetReorderForecastAsync());
        var dead = Assert.Single(await Insights.GetDeadStockAsync(30));   // the voided sale was never a sale
        Assert.Equal(oil.Id, dead.ItemId);
        Assert.Null(dead.LastSold);
        var m = await Insights.GetMorningSummaryAsync();
        Assert.Equal(Paise.Zero, m.YesterdaySales);
        Assert.Null(m.TopMoverName);
    }

    [Fact]
    public async Task MorningSummary_Yesterday_IsTheIstDay_AcrossMidnight()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6_000, stockMilli: 100_000);
        _h.Clock.Now = At(10, 17, 23, 59, 30);
        await SellAsync(_h, TenderType.Cash, null, (rice, 1_000));                   // day before yesterday
        _h.Clock.Now = At(10, 18, 0, 0, 30);
        var a = await SellAsync(_h, TenderType.Cash, null, (rice, 2_000));          // yesterday, 18:30 UTC on the 17th
        _h.Clock.Now = At(10, 18, 23, 59, 30);
        var b = await SellAsync(_h, TenderType.Upi, null, (rice, 3_000));           // yesterday
        _h.Clock.Now = At(10, 19, 0, 0, 30);
        await SellAsync(_h, TenderType.Cash, null, (rice, 4_000));                   // today
        _h.Clock.Now = At(10, 19, 9);

        var m = await Insights.GetMorningSummaryAsync();
        Assert.Equal(a.GrandTotal.Value + b.GrandTotal.Value, m.YesterdaySales.Value);
        Assert.Equal((6_000 + a.GrandTotal.Value + b.GrandTotal.Value) / 7, m.SevenDayAverage.Value);
    }

    [Fact(Skip = "BUG T2-03: dead stock reads the item's added-on date in UTC, so an item added before 05:30 IST is a day older than it is")]
    public async Task DeadStock_ItemAddedEarlyMorningIst_IsAgedByItsIstDay()
    {
        await _h.SeedShopAsync();
        _h.Clock.Now = At(9, 20, 3);                     // 20 Sep 03:00 IST = 19 Sep 21:30 UTC
        await _h.AddItemAsync("సబ్బు", 4_000, stockMilli: 5_000, unit: Unit.Piece);
        _h.Clock.Now = At(10, 19, 9);                    // 29 days later: not yet 30 days without a sale

        Assert.Empty(await Insights.GetDeadStockAsync(30));
    }
}
