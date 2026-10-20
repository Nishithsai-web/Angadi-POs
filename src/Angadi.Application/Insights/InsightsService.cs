using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Insights;

/// <summary>
/// Tier 1 "AI" (FR-31–33): plain statistics over the shop's own ledger. Offline, free, explainable —
/// every number on the card can be checked against the reports.
/// </summary>
internal sealed class InsightsService(
    IItemRepository items,
    IStockLedger ledger,
    IBillRepository bills,
    ICustomerRepository customers,
    IClock clock,
    ILocalizer text,
    IOptions<InsightsOptions> options) : IInsightsService
{
    private const int LastSoldLookbackDays = 365;

    public async Task<IReadOnlyList<ReorderForecast>> GetReorderForecastAsync(int withinDays = 7, CancellationToken ct = default)
    {
        var window = Math.Max(1, options.Value.ForecastWindowDays);
        var today = clock.Today;
        var sales = await ledger.GetDailySalesAsync(today.AddDays(-(window - 1)), today, ct);
        var soldByItem = sales.GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(s => s.Sold.Milli));

        var result = new List<ReorderForecast>();
        foreach (var item in await items.GetActiveAsync(ct))
        {
            if (!soldByItem.TryGetValue(item.Id, out var sold) || sold <= 0) continue;
            var avgPerDay = sold / (double)window;
            var daysLeft = item.Stock.Milli <= 0 ? 0 : (int)Math.Floor(item.Stock.Milli / avgPerDay);
            if (daysLeft > withinDays) continue;
            result.Add(new ReorderForecast(item.Id, text.Name(item), item.Unit, item.Stock,
                new Qty((long)Math.Round(avgPerDay, MidpointRounding.AwayFromZero)), daysLeft));
        }
        return result.OrderBy(r => r.DaysLeft).ThenBy(r => r.DisplayName, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<DeadStockItem>> GetDeadStockAsync(int days, CancellationToken ct = default)
    {
        var today = clock.Today;
        var cutoff = today.AddDays(-days);
        var sales = await ledger.GetDailySalesAsync(today.AddDays(-LastSoldLookbackDays), today, ct);
        var lastSold = sales.Where(s => s.Sold.Milli > 0).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Max(s => s.Day));

        var result = new List<DeadStockItem>();
        foreach (var item in await items.GetActiveAsync(ct))
        {
            if (item.Stock.Milli <= 0) continue;
            DateOnly? last = lastSold.TryGetValue(item.Id, out var d) ? d : null;
            var addedOn = DateOnly.FromDateTime(item.CreatedAt.DateTime);
            var dead = last is null ? addedOn <= cutoff : last.Value <= cutoff;
            if (!dead) continue;

            var value = new Paise((2 * item.Price.Value * item.Stock.Milli + 1000) / 2000);   // price × qty, half up
            result.Add(new DeadStockItem(item.Id, text.Name(item), item.Stock, value, last));
        }
        return result.OrderByDescending(r => r.StockValue.Value).ToList();
    }

    public async Task<MorningSummary> GetMorningSummaryAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var yesterday = today.AddDays(-1);
        var days = Math.Max(1, options.Value.MorningSummaryDays);
        var week = await bills.ListByDayAsync(today.AddDays(-days), yesterday, ct);
        var sold = week.Where(b => b.Status == BillStatus.Finalized && b.FinalizedAt is not null).ToList();

        var yesterdayBills = sold.Where(b => DateOnly.FromDateTime(b.FinalizedAt!.Value.DateTime) == yesterday).ToList();
        var yesterdaySales = yesterdayBills.Sum(b => b.GrandTotal.Value);
        var weekAverage = sold.Sum(b => b.GrandTotal.Value) / days;

        var topMover = yesterdayBills.SelectMany(b => b.Lines)
            .GroupBy(l => l.ItemId)
            .OrderByDescending(g => g.Sum(l => l.Net.Value))
            .Select(g => g.First())
            .Select(l => text.Name(l))
            .FirstOrDefault();

        var dues = (await customers.GetWithDuesAsync(ct)).Where(c => c.DuesBalance.Value > 0).Sum(c => c.DuesBalance.Value);
        var lowStock = (await items.GetLowStockAsync(ct)).Count;

        return new MorningSummary(new Paise(yesterdaySales), new Paise(weekAverage), topMover, new Paise(dues), lowStock);
    }
}
