using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Shop;
using Angadi.Maui.Areas;
using Angadi.Maui.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Maui;

/// <summary>
/// Six tabs, every page resolved from DI. First run asks the language (తెలుగు / English), then opens shop setup.
/// Every tab carries a one-tap language switch in the top bar.
/// </summary>
public sealed class AppShell : Shell
{
    /// <summary>Language chosen before a shop profile exists (first run); the profile wins once saved.</summary>
    public const string LanguageKey = "angadi.language";

    private readonly IServiceProvider _services;
    private readonly ILocalizer _text;
    private bool _checkedFirstRun;
    /// <summary>v2 several shops: tab pages built so far, and the open shop's name (null = one shop: no badge).</summary>
    private readonly List<Page> _pages = [];
    private string? _shopName;

    public AppShell(IServiceProvider services, ILocalizer text)
    {
        _services = services;
        _text = text;
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
        ContentTemplate = new DataTemplate(() =>
        {
            var page = _services.GetRequiredService<TPage>();
            page.ToolbarItems.Add(LanguageSwitch());
            _pages.Add(page);
            if (_shopName is not null) AddShopBadge(page);
            return page;
        }),
    };

    /// <summary>Shows the OTHER language's name, so it reads correctly to someone who can't read the current one.</summary>
    private ToolbarItem LanguageSwitch()
    {
        var item = new ToolbarItem { Text = _text.Language == UiLanguage.Telugu ? _text["lang.english"] : _text["lang.telugu"], Order = ToolbarItemOrder.Primary };
        item.Clicked += SafeUi.On(async () =>
            await SetLanguageAsync(_text.Language == UiLanguage.Telugu ? UiLanguage.English : UiLanguage.Telugu));
        return item;
    }

    private async Task SetLanguageAsync(UiLanguage language)
    {
        try
        {
            _text.Language = language;
            Preferences.Default.Set(LanguageKey, language.ToString());
            var sender = _services.GetRequiredService<ISender>();
            if (await sender.Send(new GetShopProfileQuery()) is { } profile && profile.Language != language)
            {
                profile.Language = language;
                // FIX(A1-09, 2026-10-21, S2): Flaw: a failed profile save was ignored — the language switched now and
                // silently reverted on the next restart. Fix: toast the save error (the switch still applies now).
                // Why: the shopkeeper learns the choice may not stick. Logged in docs/AGENT_FIX_LOG.md.
                var saved = await sender.Send(new SaveShopProfileCommand(profile));
                if (saved.IsFailure)
                    await _services.GetRequiredService<IDialogService>().ToastAsync(_text.Error(saved.Error));
            }
            _services.GetRequiredService<IAppRestart>().Restart();   // pages read their text when built
        }
        catch (Exception ex)
        {
            GlobalExceptions.Report(ex, "language-switch");
            // FIX(LOG-3h, 2026-10-20, FIX-3): Flaw: this failure was logged but never shown — the shopkeeper
            // tapped the language switch, nothing visibly happened, and no message explained why. Fix: show
            // the friendly UNEXPECTED toast as well. Why: a silent no-op on a visible tap reads as a broken
            // app. Logged in docs/AGENT_FIX_LOG.md.
            await SafeUi.ShowUnexpectedAsync();
        }
    }

    /// <summary>v2 several shops: the open shop's name in the top bar; a tap opens Settings → Shops.</summary>
    private void AddShopBadge(Page page)
    {
        if (page.ToolbarItems.Any(t => string.Equals(t.ClassId, ShopBadgeId, StringComparison.Ordinal))) return;
        var badge = new ToolbarItem { Text = _shopName, Order = ToolbarItemOrder.Primary, ClassId = ShopBadgeId };
        badge.Clicked += SafeUi.On(async () => await GoToAsync(Routes.Shops));
        page.ToolbarItems.Insert(0, badge);
    }

    private const string ShopBadgeId = "angadi.shop-badge";

    private async Task ShowShopNameAsync()
    {
        try
        {
            var shops = await _services.GetRequiredService<ISender>().Send(new Angadi.Application.Shops.ListShopsQuery());
            if (!shops.HasSeveral) return;
            _shopName = shops.Current.Name.Length > 0 ? shops.Current.Name : _text["shops.unnamed"];
            foreach (var page in _pages) AddShopBadge(page);
        }
        catch (Exception ex)
        {
            GlobalExceptions.Report(ex, "shop-badge");   // cosmetic: never blocks the counter
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_checkedFirstRun) return;
        _checkedFirstRun = true;
        await ShowShopNameAsync();
        try
        {
            var profile = await _services.GetRequiredService<ISender>().Send(new GetShopProfileQuery());
            if (profile is not null) return;

            // First run: language first, in both scripts, before any other screen is built.
            if (!Preferences.Default.ContainsKey(LanguageKey))
            {
                var pick = await DisplayActionSheetAsync(_text["lang.choose"], null, null, _text["lang.telugu"], _text["lang.english"]);
                var language = pick == _text["lang.english"] ? UiLanguage.English : UiLanguage.Telugu;
                Preferences.Default.Set(LanguageKey, language.ToString());
                if (language != _text.Language)
                {
                    _text.Language = language;
                    _services.GetRequiredService<IAppRestart>().Restart();   // rebuild tabs in the chosen language
                    return;
                }
            }
            await GoToAsync(Routes.ShopSetup);
        }
        catch (Exception ex)
        {
            GlobalExceptions.Report(ex, "first-run");
            // FIX(LOG-3h, 2026-10-20, FIX-3): Flaw: a first-run failure was logged but never shown — the
            // shopkeeper could land on an empty POS with no setup prompt and no message. Fix: show the
            // friendly UNEXPECTED toast as well. Why: first run is the app's first impression; silence here
            // looks like abandonment. Logged in docs/AGENT_FIX_LOG.md.
            await SafeUi.ShowUnexpectedAsync();
        }
    }
}
