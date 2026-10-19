using Angadi.Maui.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Maui.Areas;

public static class PagesRegistration
{
    /// <summary>Tab pages are created by AppShell from DI; detail pages get Shell routes.</summary>
    public static IServiceCollection AddAppPages(this IServiceCollection s)
    {
        s.AddTransient<PosViewModel>();
        s.AddTransient<PosPage>();
        s.AddTransient<ItemsPage>();
        s.AddTransient<BillsPage>();
        s.AddTransient<CustomersPage>();
        s.AddTransient<ReportsPage>();
        s.AddTransient<SettingsPage>();

        Route<ReceiptPage>(s, Routes.Receipt);
        Route<UpiPayPage>(s, Routes.UpiPay);
        Route<ItemEditPage>(s, Routes.ItemEdit);
        Route<BillDetailPage>(s, Routes.BillDetail);
        Route<CustomerDetailPage>(s, Routes.CustomerDetail);
        Route<ShopSetupPage>(s, Routes.ShopSetup);
        Route<SubscriptionPage>(s, Routes.Subscription);
        return s;
    }

    private static void Route<TPage>(IServiceCollection s, string route) where TPage : Page
    {
        s.AddTransient<TPage>();
        Routing.RegisterRoute(route, typeof(TPage));
    }
}
