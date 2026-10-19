using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Maui.Areas;
using Angadi.Maui.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Maui;

/// <summary>
/// Six tabs, every page resolved from DI. First run (no shop profile) opens shop setup before anything else.
/// </summary>
public sealed class AppShell : Shell
{
    private readonly IServiceProvider _services;
    private bool _checkedFirstRun;

    public AppShell(IServiceProvider services, ILocalizer text)
    {
        _services = services;
        FlyoutBehavior = FlyoutBehavior.Disabled;
        Shell.SetBackgroundColor(this, Theme.Primary);
        Shell.SetForegroundColor(this, Theme.OnPrimary);
        Shell.SetTitleColor(this, Theme.OnPrimary);
        Shell.SetTabBarBackgroundColor(this, Theme.Surface);
        Shell.SetTabBarForegroundColor(this, Theme.Primary);
        Shell.SetTabBarTitleColor(this, Theme.Primary);
        Shell.SetTabBarUnselectedColor(this, Theme.TextSecondary);

        var tabs = new TabBar();
        tabs.Items.Add(Tab<PosPage>(text["nav.pos"], Routes.Pos));
        tabs.Items.Add(Tab<ItemsPage>(text["nav.items"], Routes.Items));
        tabs.Items.Add(Tab<BillsPage>(text["nav.bills"], Routes.Bills));
        tabs.Items.Add(Tab<CustomersPage>(text["nav.customers"], Routes.Customers));
        tabs.Items.Add(Tab<ReportsPage>(text["nav.reports"], Routes.Reports));
        tabs.Items.Add(Tab<SettingsPage>(text["nav.settings"], Routes.Settings));
        Items.Add(tabs);
    }

    private ShellContent Tab<TPage>(string title, string route) where TPage : Page => new()
    {
        Title = title,
        Route = route,
        ContentTemplate = new DataTemplate(() => _services.GetRequiredService<TPage>()),
    };

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_checkedFirstRun) return;
        _checkedFirstRun = true;
        try
        {
            var profile = await _services.GetRequiredService<ISender>().Send(new GetShopProfileQuery());
            if (profile is null) await GoToAsync(Routes.ShopSetup);
        }
        catch (Exception ex)
        {
            GlobalExceptions.Report(ex, "first-run");
        }
    }
}
