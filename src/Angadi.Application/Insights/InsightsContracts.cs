using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Application.Insights;

/// <summary>
/// Tier 1 on-device AI (FR-31–33): plain statistics over the StockMove ledger, offline, free.
/// Tier 2 LLM features are v2 (SRS D2) — no IAiService in v1.
/// </summary>
public interface IInsightsService
{
    /// <summary>FR-31. Items likely to run out within <paramref name="withinDays"/>, soonest first.</summary>
    Task<IReadOnlyList<ReorderForecast>> GetReorderForecastAsync(int withinDays = 7, CancellationToken ct = default);

    /// <summary>FR-32. Items with stock but no sale for at least <paramref name="days"/> days.</summary>
    Task<IReadOnlyList<DeadStockItem>> GetDeadStockAsync(int days, CancellationToken ct = default);

    /// <summary>FR-33. Morning card.</summary>
    Task<MorningSummary> GetMorningSummaryAsync(CancellationToken ct = default);
}

/// <summary>DaysLeft = Stock ÷ average daily sales over InsightsOptions.ForecastWindowDays.</summary>
public sealed record ReorderForecast(Guid ItemId, string DisplayName, Unit Unit, Qty Stock, Qty AvgDailySales, int DaysLeft);

public sealed record DeadStockItem(Guid ItemId, string DisplayName, Qty Stock, Paise StockValue, DateOnly? LastSold);

public sealed record MorningSummary(
    Paise YesterdaySales, Paise SevenDayAverage, string? TopMoverName,
    Paise DuesToCollect, int LowStockCount);
