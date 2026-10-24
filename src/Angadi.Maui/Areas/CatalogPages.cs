using System.Collections.ObjectModel;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Voice;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Maui.Areas.Pos;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

public sealed record ItemListRow(Guid Id, string Name, string Detail, string Stock, Color StockColor);

/// <summary>FR-03/05/25: item list with search, low-stock highlight, add.</summary>
public sealed class ItemsPage : ContentPage
{
    private readonly ISender _sender;
    private readonly ILocalizer _text;
    private readonly ObservableCollection<ItemListRow> _rows = [];
    private readonly Entry _search;
    private readonly Label _summary = Ui.Caption();
    private readonly Label _legacy = new() { TextColor = Theme.Warning, FontAttributes = FontAttributes.Bold, IsVisible = false };
    private IReadOnlyList<Item> _all = [];

    public ItemsPage(ISender sender, ILocalizer text, IBarcodeScanner scanner, IDialogService dialogs)
    {
        _sender = sender;
        _text = text;
        Title = text["nav.items"];
        _search = Ui.Entry(text["pos.search"]);
        _search.TextChanged += (_, _) => Filter();
        var add = Ui.Button("+ " + text["items.add"]);
        add.Clicked += SafeUi.On(async () => await Shell.Current.GoToAsync(Routes.ItemEdit));
        // Scan a packet: open its item, or start a new item with the code already filled in.
        var scan = Ui.Secondary("▦");
        SemanticProperties.SetDescription(scan, text["items.scan.find"]);
        scan.Clicked += SafeUi.On(async () =>
        {
            var r = await scanner.ScanAsync();
            if (r.IsFailure) return;
            if (Barcodes.Normalize(r.Value) is not { } code || !Barcodes.IsValid(code))
            {
                await dialogs.ToastAsync(text.Error(new Error(ErrorCodes.BarcodeInvalid)));
                return;
            }
            var item = await _sender.Send(new GetItemByBarcodeQuery(code));
            var args = item is not null
                ? new Dictionary<string, object> { ["itemId"] = item.Id.ToString("D") }
                : new Dictionary<string, object> { ["barcode"] = code };
            await Shell.Current.GoToAsync(Routes.ItemEdit, args);
        });

        var list = new CollectionView { ItemsSource = _rows, SelectionMode = SelectionMode.Single, ItemTemplate = RowTemplate(), EmptyView = Ui.Caption(text["common.empty"]) };
        list.SelectionChanged += SafeUi.On<SelectionChangedEventArgs>(async e =>
        {
            if (e.CurrentSelection.FirstOrDefault() is ItemListRow row)
                await Shell.Current.GoToAsync(Routes.ItemEdit, new Dictionary<string, object> { ["itemId"] = row.Id.ToString("D") });
            list.SelectedItem = null;
        });

        var top = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) }, ColumnSpacing = 8 };
        top.Add(_search, 0, 0);
        top.Add(scan, 1, 0);
        top.Add(add, 2, 0);
        var grid = new Grid { Padding = 12, RowSpacing = 8, RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) } };
        grid.Add(top, 0, 0);
        grid.Add(_legacy, 0, 1);
        grid.Add(_summary, 0, 2);
        grid.Add(list, 0, 3);
        Content = grid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // FIX(UI-1, 2026-10-20, FIX-3): Flaw: this page derives from ContentPage (not PageBase) and awaited sender
        // calls unwrapped in async void — a failure escaped SafeUi, leaving a half-loaded page and no toast.
        // Fix: the whole load runs through SafeUi.Run (log + UNEXPECTED toast, page stays usable). Why: the
        // "never-fail UI" guarantee must cover every page, not only the POS. Logged in docs/AGENT_FIX_LOG.md.
        await SafeUi.Run(async () =>
        {
            _all = await _sender.Send(new GetAllItemsQuery());
            var low = _all.Count(i => i.IsLowStock);
            _summary.Text = low > 0 ? $"{_text["stock.low.title"]}: {low}" : "";
            // H8: 12%/28% items cannot be billed under GST 2.0 — say so here, before the counter finds out.
            var legacy = await _sender.Send(new GetLegacySlabItemsQuery());
            _legacy.IsVisible = legacy.Count > 0;
            _legacy.Text = legacy.Count > 0 ? _text.Format("items.legacy.notice", legacy.Count) : "";
            Filter();
        });
    }

    private void Filter()
    {
        var q = TeluguText.Normalize(_search.Text);
        _rows.Clear();
        foreach (var i in _all.Where(i => q.Length == 0
                     || TeluguText.Normalize(i.TeluguName).Contains(q, StringComparison.Ordinal)
                     || TeluguText.Normalize(i.EnglishName).Contains(q, StringComparison.Ordinal)
                     || i.Aliases.Any(a => TeluguText.Normalize(a).Contains(q, StringComparison.Ordinal))
                     || (i.Barcode ?? "").StartsWith(q, StringComparison.Ordinal)))
        {
            _rows.Add(new ItemListRow(i.Id, _text.Name(i),
                $"{i.EnglishName} · {IndianFormat.Rupees(i.Price)} / {_text.UnitLabel(i.Unit)} · GST {(int)i.GstSlab / 100}%",
                $"{IndianFormat.Quantity(i.Stock)} {_text.UnitLabel(i.Unit)}",
                i.IsLowStock ? Theme.Danger : Theme.TextSecondary));
        }
    }

    private static DataTemplate RowTemplate() => new(() =>
    {
        var name = new Label { FontFamily = Theme.FontBold, FontSize = 17 };
        name.SetBinding(Label.TextProperty, nameof(ItemListRow.Name));
        var detail = Ui.Caption();
        detail.SetBinding(Label.TextProperty, nameof(ItemListRow.Detail));
        var stock = new Label { FontFamily = Theme.FontBold, HorizontalTextAlignment = TextAlignment.End, VerticalTextAlignment = TextAlignment.Center };
        stock.SetBinding(Label.TextProperty, nameof(ItemListRow.Stock));
        stock.SetBinding(Label.TextColorProperty, nameof(ItemListRow.StockColor));
        var g = new Grid { Padding = new Thickness(4, 8), ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        g.Add(new VerticalStackLayout { Children = { name, detail } }, 0, 0);
        g.Add(stock, 1, 0);
        return g;
    });
}

/// <summary>FR-03/08/24: create or edit an item; stock-in / correction for existing items.</summary>
public sealed class ItemEditPage : ContentPage, IQueryAttributable
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly IBarcodeScanner _scanner;
    private readonly Entry _te, _en, _aliases, _category, _price, _barcode, _low, _opening;
    private readonly Picker _unit = new();
    private readonly Picker _slab = new();
    private readonly Switch _quick = new() { OnColor = Theme.Primary };
    private readonly View _stockBox;
    private readonly Label _stockNow = Ui.Body();
    private readonly View _openingBox;
    private Item _item = new();
    private bool _isNew = true;

    private static readonly Unit[] Units = Enum.GetValues<Unit>();

    public ItemEditPage(ISender sender, IDialogService dialogs, ILocalizer text, IBarcodeScanner scanner)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        _scanner = scanner;
        Title = text["items.add"];
        _te = Ui.Entry(text["items.telugu"]);
        _en = Ui.Entry(text["items.english"]);
        _aliases = Ui.Entry(text["items.aliases.hint"]);
        _category = Ui.Entry(text["items.category"]);
        _price = Ui.Entry(text["items.price"], Keyboard.Numeric);
        _barcode = Ui.Entry(text["items.barcode"]);   // Code 128 / shop codes may contain letters
        _low = Ui.Entry(text["items.lowstock"], Keyboard.Numeric);
        _opening = Ui.Entry(text["items.opening"], Keyboard.Numeric);
        foreach (var u in Units) _unit.Items.Add(text.UnitLabel(u));
        foreach (var s in GstSlabs.Current) _slab.Items.Add($"{(int)s / 100}%");
        _unit.SelectedIndex = 0;
        _slab.SelectedIndex = 1;

        var scan = Ui.Secondary("▦");
        scan.Clicked += SafeUi.On(async () =>
        {
            var r = await _scanner.ScanAsync();
            if (r.IsFailure) return;
            if (Barcodes.Normalize(r.Value) is not { } code || !Barcodes.IsValid(code))
            {
                await _dialogs.ToastAsync(_text.Error(new Error(ErrorCodes.BarcodeInvalid)));
                return;
            }
            // Tell the shopkeeper at once if another item already has this packet's code.
            var owner = await _sender.Send(new GetItemByBarcodeQuery(code));
            if (owner is not null && owner.Id != _item.Id)
            {
                await _dialogs.ToastAsync(_text.Error(Error.With(ErrorCodes.BarcodeDuplicate, _text.Name(owner))));
                return;
            }
            _barcode.Text = code;
        });
        var barcodeRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        barcodeRow.Add(_barcode, 0, 0);
        barcodeRow.Add(scan, 1, 0);

        var stockIn = Ui.Secondary(text["stock.in"]);
        stockIn.Clicked += SafeUi.On(async () => await AdjustAsync(StockMoveReason.Purchase));
        var damage = Ui.Secondary(text["stock.reason.damage"]);
        damage.Clicked += SafeUi.On(async () => await AdjustAsync(StockMoveReason.Damage));
        var count = Ui.Secondary(text["stock.reason.count"]);
        count.Clicked += SafeUi.On(async () => await AdjustAsync(StockMoveReason.CountCorrection));
        _stockBox = Ui.Card(new VerticalStackLayout { Spacing = 8, Children = { Ui.Caption(text["items.stock"]), _stockNow, stockIn, damage, count } });
        _openingBox = new VerticalStackLayout { Children = { Ui.Caption(text["items.opening"]), _opening } };

        var save = Ui.Button(text["items.save"], big: true);
        save.Clicked += SafeUi.On(async () => await SaveAsync());

        Content = Ui.Scroll(
            Ui.Caption(text["items.telugu"]), _te,
            Ui.Caption(text["items.english"]), _en,
            Ui.Caption(text["items.aliases"]), _aliases,
            Ui.Caption(text["items.category"]), _category,
            Ui.Caption(text["items.unit"]), _unit,
            Ui.Caption(text["items.price"]), _price,
            Ui.Caption(text["items.gst"]), _slab,
            Ui.Caption(text["items.barcode"]), barcodeRow,
            Ui.Caption(text["items.lowstock"]), _low,
            new HorizontalStackLayout { Spacing = 8, Children = { _quick, new Label { Text = text["items.quicksale"], VerticalTextAlignment = TextAlignment.Center } } },
            _openingBox,
            _stockBox,
            save);
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("barcode", out var code)) _barcode.Text = code?.ToString();
        if (query.TryGetValue("itemId", out var id) && Guid.TryParse(id?.ToString(), out var itemId)) _ = SafeUi.Run(() => LoadAsync(itemId));   // A1-03: logged and toasted, not lost
        else Show();
    }

    private async Task LoadAsync(Guid id)
    {
        var item = await _sender.Send(new GetItemQuery(id));
        if (item is null) return;
        _item = item;
        _isNew = false;
        Title = _text.Name(item);
        _te.Text = item.TeluguName;
        _en.Text = item.EnglishName;
        _aliases.Text = string.Join(", ", item.Aliases);
        _category.Text = item.Category;
        _unit.SelectedIndex = Array.IndexOf(Units, item.Unit);
        var slabIndex = GstSlabs.Current.ToList().IndexOf(item.GstSlab);
        _slab.SelectedIndex = slabIndex;   // −1 for a legacy slab: the shopkeeper must pick a current one (H8)
        _price.Text = (item.Price.Value / 100m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        _barcode.Text = item.Barcode;
        _low.Text = IndianFormat.Quantity(item.LowStockThreshold);
        _quick.IsToggled = item.IsQuickSale;
        Show();
    }

    private void Show()
    {
        _stockBox.IsVisible = !_isNew;
        _openingBox.IsVisible = _isNew;
        _stockNow.Text = $"{IndianFormat.Quantity(_item.Stock)} {_text.UnitLabel(_item.Unit)}";
    }

    private async Task SaveAsync()
    {
        if (!PosInput.TryParseRupees(_price.Text, out var price)) { await _dialogs.ToastAsync(_text["items.price"] + " ?"); return; }
        if (_slab.SelectedIndex < 0) { await _dialogs.ToastAsync(_text.Error(new Error(ErrorCodes.BillLegacySlab))); return; }
        _item.TeluguName = _te.Text ?? "";
        _item.EnglishName = _en.Text ?? "";
        _item.Aliases = (_aliases.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _item.Category = _category.Text ?? "";
        _item.Unit = Units[Math.Max(0, _unit.SelectedIndex)];
        _item.Price = price;
        _item.GstSlab = GstSlabs.Current[_slab.SelectedIndex];
        _item.Barcode = string.IsNullOrWhiteSpace(_barcode.Text) ? null : _barcode.Text.Trim();
        _item.LowStockThreshold = PosInput.TryParseQty(_low.Text, out var low) ? low : Qty.Zero;
        _item.IsQuickSale = _quick.IsToggled;
        Qty? opening = _isNew && PosInput.TryParseQty(_opening.Text, out var o) && o.Milli > 0 ? o : null;

        var r = await _sender.Send(new SaveItemCommand(_item, opening));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        await Shell.Current.GoToAsync("..");
    }

    private async Task AdjustAsync(StockMoveReason reason)
    {
        var prompt = reason == StockMoveReason.CountCorrection ? _text["stock.count.prompt"] : _text["bill.qty"];
        var value = await _dialogs.PromptAsync(_text["stock.adjust"], prompt, _text["common.ok"], _text["common.cancel"], keyboard: Keyboard.Numeric);
        if (value is null || !PosInput.TryParseQty(value, out var qty)) return;
        var delta = reason switch
        {
            StockMoveReason.CountCorrection => qty - _item.Stock,   // user enters the counted stock
            StockMoveReason.Damage => -qty,
            _ => qty,
        };
        if (delta.Milli == 0) return;
        var r = await _sender.Send(new AdjustStockCommand(_item.Id, delta, reason, null));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        await LoadAsync(_item.Id);
    }
}
