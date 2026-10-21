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
        Route<NotesPage>(s, Routes.Notes);
        return s;
    }

    private static void Route<TPage>(IServiceCollection s, string route) where TPage : Page
    {
        s.AddTransient<TPage>();
        // Not RegisterRoute(route, typeof(TPage)): that path builds the page with Activator when Shell can't reach the
        // MAUI service provider at that moment (seen on the trimmed Release build at first run: MissingMethodException
        // "no parameterless constructor" for ShopSetupPage, leaving a new shop stuck). Pages have DI constructors only.
        Routing.RegisterRoute(route, new DiRouteFactory<TPage>());
    }

    /// <summary>Shell route factory that always builds the page from the app's own service provider.</summary>
    private sealed class DiRouteFactory<TPage> : RouteFactory where TPage : Page
    {
        public override Element GetOrCreate() => Create(null);
        public override Element GetOrCreate(IServiceProvider services) => Create(services);

        private static TPage Create(IServiceProvider? fromShell) =>
            (SafeUi.Services ?? fromShell ?? throw new InvalidOperationException("App services are not ready"))
                .GetRequiredService<TPage>();
    }
}
