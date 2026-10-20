namespace Angadi.Application.Common;

/// <summary>Bound from appsettings.json "Voice". Thresholds per FR-07.</summary>
public sealed class VoiceOptions
{
    public string Culture { get; set; } = "te-IN";
    public double AutoAddThreshold { get; set; } = 0.90;
    public double ConfirmThreshold { get; set; } = 0.70;
    public int MaxCandidates { get; set; } = 5;   // 3 hid two of five dals on a generic "పప్పు"
    /// <summary>M2: learned aliases kept per item before least-recently-used eviction. Curated aliases are never counted.</summary>
    public int MaxLearnedAliasesPerItem { get; set; } = 30;
}

/// <summary>Bound from appsettings.json "Billing". FR-34/35.</summary>
public sealed class BillingOptions
{
    public int FreeBillsPerMonth { get; set; } = 50;
    public string MonthlyProductId { get; set; } = "angadi_pro_monthly";   // ₹200
    public string YearlyProductId { get; set; } = "angadi_pro_yearly";     // ₹1,999
    /// <summary>M10: prices shown in the app come from here (later from Play), never from string files.</summary>
    public int MonthlyPriceRupees { get; set; } = 200;
    public int YearlyPriceRupees { get; set; } = 1999;
}

/// <summary>Bound from appsettings.json "Insights". FR-31–33.</summary>
public sealed class InsightsOptions
{
    public int ForecastWindowDays { get; set; } = 14;
    public int[] DeadStockDays { get; set; } = [30, 60, 90];
    /// <summary>FR-33: the morning summary compares yesterday with the average of this many days.</summary>
    public int MorningSummaryDays { get; set; } = 7;
}

/// <summary>Bound from appsettings.json "Support". Where in-app feedback goes.</summary>
public sealed class SupportOptions
{
    /// <summary>Support WhatsApp number (10-digit Indian mobile). Empty → feedback opens the share sheet.</summary>
    public string WhatsAppNumber { get; set; } = "";
}
