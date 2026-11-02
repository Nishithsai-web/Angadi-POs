using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Shops;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

/// <summary>
/// v2 Settings → Shops: several shops on one phone. Tap a shop to open it (the shell is rebuilt on that shop's data);
/// "Rename" and "+ Add a shop" go through the owner PIN gate. Free tier: one shop — adding asks for Pro.
/// </summary>
public sealed class ShopsPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly IAppRestart _restart;
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };

    public ShopsPage(ISender sender, IDialogService dialogs, ILocalizer text, IAppRestart restart)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        _restart = restart;
        Title = text["settings.shops"];
        var add = Ui.Button(text["shops.add"], big: true);
        add.Clicked += SafeUi.On(AddAsync);
        Content = Ui.Scroll(Ui.Title(text["shops.title"]), Ui.Caption(text["shops.hint"]), _list, add);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await SafeUi.Run(RefreshAsync);
    }

    private string NameOf(ShopEntry s) => s.Name.Length > 0 ? s.Name : _text["shops.unnamed"];

    private async Task RefreshAsync()
    {
        var shops = await _sender.Send(new ListShopsQuery());
        _list.Clear();
        foreach (var shop in shops.Shops)
        {
            var open = shop.IsCurrent
                ? Ui.Button(_text.Format("shops.open.now", NameOf(shop)), Theme.Primary)
                : Ui.Secondary(NameOf(shop));
            open.HorizontalOptions = LayoutOptions.Fill;
            var id = shop.Id;
            var name = NameOf(shop);
            if (!shop.IsCurrent) open.Clicked += SafeUi.On(async () => await SwitchAsync(id, name));
            var rename = Ui.Secondary(_text["shops.rename"]);
            rename.Clicked += SafeUi.On(async () => await RenameAsync(id, shop.Name));
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            row.Add(open, 0);
            row.Add(rename, 1);
            _list.Add(row);
        }
    }

    private async Task SwitchAsync(string id, string name)
    {
        if (!await _dialogs.ConfirmAsync(_text["shops.switch.title"], _text.Format("shops.switch.confirm", name), _text["common.yes"], _text["common.no"])) return;
        var r = await _sender.Send(new SwitchShopCommand(id));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        _restart.Restart();   // every page is rebuilt on the opened shop's data
    }

    private async Task RenameAsync(string id, string current)
    {
        if (!await PinGate.CheckAsync(Angadi.Application.Security.Permission.ManageShops, _dialogs, _text)) return;
        var name = await _dialogs.PromptAsync(_text["shops.rename"], _text["shops.rename.title"], _text["common.ok"], _text["common.cancel"], current);
        if (name is null) return;
        var r = await _sender.Send(new RenameShopCommand(id, name));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        _restart.Restart();   // the top bar shows the new name
    }

    private async Task AddAsync()
    {
        if (!await PinGate.CheckAsync(Angadi.Application.Security.Permission.ManageShops, _dialogs, _text)) return;
        if (!await _dialogs.ConfirmAsync(_text["shops.add"], _text["shops.add.confirm"], _text["common.yes"], _text["common.no"])) return;
        var r = await _sender.Send(new AddShopCommand());
        if (r.IsFailure)
        {
            if (string.Equals(r.Error.Code, ErrorCodes.ShopNeedsPro, StringComparison.Ordinal))
            {
                // A dialog, not a toast: the owner can go straight to the plans.
                if (await _dialogs.ConfirmAsync(_text["shops.needspro.title"], _text.Error(r.Error), _text["shops.plans"], _text["common.cancel"]))
                    await Shell.Current.GoToAsync(Routes.Subscription);
                return;
            }
            await _dialogs.ToastAsync(_text.Error(r.Error));
            return;
        }
        _restart.Restart();   // the new, empty shop opens on normal shop setup
    }
}
