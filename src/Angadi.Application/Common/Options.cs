namespace Angadi.Application.Common;

/// <summary>Bound from appsettings.json "Voice". Thresholds per FR-07.</summary>
public sealed class VoiceOptions
{
    public string Culture { get; set; } = "te-IN";
    public double AutoAddThreshold { get; set; } = 0.90;
    public double ConfirmThreshold { get; set; } = 0.70;
    public int MaxCandidates { get; set; } = 3;
}

/// <summary>Bound from appsettings.json "Billing". FR-34/35.</summary>
public sealed class BillingOptions
{
    public int FreeBillsPerMonth { get; set; } = 50;
    public string MonthlyProductId { get; set; } = "angadi_pro_monthly";   // ₹200
    public string YearlyProductId { get; set; } = "angadi_pro_yearly";     // ₹1,999
}

/// <summary>Bound from appsettings.json "Insights". FR-31–33.</summary>
public sealed class InsightsOptions
{
    public int ForecastWindowDays { get; set; } = 14;
    public int[] DeadStockDays { get; set; } = [30, 60, 90];
}
