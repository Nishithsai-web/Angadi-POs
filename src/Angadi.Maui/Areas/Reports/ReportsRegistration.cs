using Angadi.Maui.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Maui.Areas.Reports;

public static class ReportsRegistration
{
    public static IServiceCollection AddReportsArea(this IServiceCollection s)
    {
        // Tab page (resolved by AppShell).
        s.AddTransient<ReportsViewModel>();
        s.AddTransient<ReportsPage>();

        Routing.RegisterRoute(Routes.Insights, typeof(InsightsPage));
        s.AddTransient<InsightsViewModel>();
        s.AddTransient<InsightsPage>();
        return s;
    }
}
