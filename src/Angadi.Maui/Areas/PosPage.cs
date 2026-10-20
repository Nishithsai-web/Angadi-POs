using Angadi.Application.Billing;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Maui.Core;
using Microsoft.Maui.Controls.Shapes;
using QRCoder;

namespace Angadi.Maui.Areas;

/// <summary>
/// The counter screen (docs/UI_SPEC.md). Tablet (≥ 900 dp wide): catalogue left, bill right.
/// Phone: catalogue on top, bill below. Mic always visible, tender buttons ≥ 64 dp.
/// </summary>
public sealed class PosPage : PageBase
{
    private readonly Grid _root = new() { Padding = new Thickness(12), ColumnSpacing = 12, RowSpacing = 12 };
    private readonly View _left;
    private readonly View _right;
    private bool? _wide;

    public PosPage(PosViewModel vm) : base(vm)
    {
        Title = vm.Text["nav.pos"];
        _left = BuildCatalogue(vm);
        _right = BuildBill(vm);
        Content = _root;
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        var wide = width >= 900;
        if (_wide == wide) return;
        _wide = wide;
        _root.Children.Clear();
        _root.ColumnDefinitions.Clear();
        _root.RowDefinitions.Clear();
        if (wide)
        {
            _root.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(3, GridUnitType.Star)));
            _root.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(2, GridUnitType.Star)));
            _root.Add(_left, 0, 0);
            _root.Add(_right, 1, 0);
        }
        else
        {
            _root.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            _root.RowDefinitions.Add(new RowDefinition(new GridLength(1.3, GridUnitType.Star)));
            _root.Add(_left, 0, 0);
            _root.Add(_right, 0, 1);
        }
    }

    private static View BuildCatalogue(PosViewModel vm)
    {
        var mic = new Button
        {
            Text = "🎤  " + vm.Text["pos.mic"], FontSize = 20, MinimumHeightRequest = 72, BackgroundColor = Theme.Accent, TextColor = Theme.TextPrimary,
        };
        mic.SetBinding(Button.CommandProperty, nameof(PosViewModel.MicCommand));
        var scan = Ui.Secondary("▦  " + vm.Text["pos.scan"]);
        scan.SetBinding(Button.CommandProperty, nameof(PosViewModel.ScanCommand));

        var voiceText = Ui.Caption();
        voiceText.SetBinding(Label.TextProperty, nameof(PosViewModel.VoiceText));

        var search = Ui.Entry(vm.Text["pos.search"]);
        search.SetBinding(Entry.TextProperty, nameof(PosViewModel.SearchText));

        var results = new CollectionView { SelectionMode = SelectionMode.Single, ItemTemplate = TileTemplate(), MaximumHeightRequest = 240 };
        results.SetBinding(ItemsView.ItemsSourceProperty, nameof(PosViewModel.SearchResults));
        results.SelectionChanged += (_, e) => Pick(vm, results, e);

        var quick = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = TileTemplate(),
            ItemsLayout = new GridItemsLayout(3, ItemsLayoutOrientation.Vertical) { HorizontalItemSpacing = 8, VerticalItemSpacing = 8 },
        };
        quick.SetBinding(ItemsView.ItemsSourceProperty, nameof(PosViewModel.QuickSale));
        quick.SelectionChanged += (_, e) => Pick(vm, quick, e, quickSale: true);

        var actions = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        actions.Add(mic, 0, 0);
        actions.Add(scan, 1, 0);

        var grid = new Grid
        {
            RowSpacing = 8,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
        };
        grid.Add(actions, 0, 0);
        grid.Add(voiceText, 0, 1);
        grid.Add(search, 0, 2);
        grid.Add(results, 0, 3);
        grid.Add(Ui.Caption(vm.Text["pos.quicksale"]), 0, 4);
        grid.Add(quick, 0, 5);
        return grid;
    }

    private static void Pick(PosViewModel vm, CollectionView list, SelectionChangedEventArgs e, bool quickSale = false)
    {
        if (e.CurrentSelection.FirstOrDefault() is ItemTile tile)
            vm.Add(tile, quickSale ? LineSource.QuickSale : LineSource.Manual);
        list.SelectedItem = null;
    }

    private static DataTemplate TileTemplate() => new(() =>
    {
        var name = new Label { FontFamily = Theme.FontBold, FontSize = 16, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(ItemTile.Name));
        var price = Ui.Caption();
        price.SetBinding(Label.TextProperty, nameof(ItemTile.Price));
        return Ui.Card(new VerticalStackLayout { Spacing = 2, Children = { name, price } }, 10);
    });

    private static View BuildBill(PosViewModel vm)
    {
        var lines = new CollectionView { ItemTemplate = LineTemplate(vm), SelectionMode = SelectionMode.None };
        lines.SetBinding(ItemsView.ItemsSourceProperty, nameof(PosViewModel.Lines));
        lines.EmptyView = Ui.StateLine(nameof(PosViewModel.EmptyText));

        var totals = new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                Ui.Row(vm.Text["bill.subtotal"], nameof(PosViewModel.SubTotal)),
                Ui.Row(vm.Text["bill.discount"], nameof(PosViewModel.Discount)),
                Ui.Row(vm.Text["bill.tax"], nameof(PosViewModel.Tax)),
                Ui.Row(vm.Text["bill.rounding"], nameof(PosViewModel.Rounding)),
            },
        };
        var total = Ui.Amount();
        total.SetBinding(Label.TextProperty, nameof(PosViewModel.Total));
        var quota = Ui.Caption();
        quota.SetBinding(Label.TextProperty, nameof(PosViewModel.QuotaText));
        var customer = Ui.Secondary("");
        customer.SetBinding(Button.TextProperty, nameof(PosViewModel.CustomerLabel));
        customer.SetBinding(Button.CommandProperty, nameof(PosViewModel.AttachCustomerCommand));

        var tools = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        var discount = Ui.Secondary(vm.Text["pos.discount.bill"]);
        discount.SetBinding(Button.CommandProperty, nameof(PosViewModel.BillDiscountCommand));
        var newBill = Ui.Secondary(vm.Text["pos.newbill"]);
        newBill.SetBinding(Button.CommandProperty, nameof(PosViewModel.NewBillCommand));
        tools.Add(discount, 0, 0);
        tools.Add(newBill, 1, 0);

        var tender = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        tender.Add(TenderButton(vm.Text["tender.cash"], Theme.CashColor, nameof(PosViewModel.PayCashCommand)), 0, 0);
        tender.Add(TenderButton(vm.Text["tender.upi"], Theme.UpiColor, nameof(PosViewModel.PayUpiCommand)), 1, 0);
        tender.Add(TenderButton(vm.Text["tender.udhaar"], Theme.UdhaarColor, nameof(PosViewModel.PayUdhaarCommand)), 2, 0);

        var busy = new ActivityIndicator { Color = Theme.Primary };
        busy.SetBinding(ActivityIndicator.IsRunningProperty, nameof(ViewModelBase.IsBusy));
        busy.SetBinding(VisualElement.IsVisibleProperty, nameof(ViewModelBase.IsBusy));

        var grid = new Grid
        {
            RowSpacing = 8,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) },
        };
        grid.Add(customer, 0, 0);
        grid.Add(lines, 0, 1);
        grid.Add(totals, 0, 2);
        grid.Add(total, 0, 3);
        grid.Add(quota, 0, 4);
        grid.Add(tools, 0, 5);
        grid.Add(busy, 0, 6);
        grid.Add(tender, 0, 7);
        return Ui.Card(grid);
    }

    private static Button TenderButton(string text, Color color, string commandPath)
    {
        var b = Ui.Button(text, color, big: true);
        b.SetBinding(Button.CommandProperty, commandPath);
        return b;
    }

    private static DataTemplate LineTemplate(PosViewModel vm) => new(() =>
    {
        var name = new Label { FontFamily = Theme.FontBold, FontSize = 16 };
        name.SetBinding(Label.TextProperty, nameof(LineRow.Name));
        var qty = Ui.Caption();
        qty.SetBinding(Label.TextProperty, nameof(LineRow.QtyText));
        var amount = new Label { FontFamily = Theme.FontBold, FontSize = 16, HorizontalTextAlignment = TextAlignment.End, VerticalTextAlignment = TextAlignment.Center };
        amount.SetBinding(Label.TextProperty, nameof(LineRow.Amount));

        Button Small(string glyph, string command)
        {
            var b = new Button { Text = glyph, WidthRequest = 48, BackgroundColor = Theme.PrimarySoft, TextColor = Theme.Primary, Padding = 0 };
            b.SetBinding(Button.CommandProperty, new Binding(command, source: vm));
            b.SetBinding(Button.CommandParameterProperty, ".");
            return b;
        }

        var info = new VerticalStackLayout { Children = { name, qty } };
        info.GestureRecognizers.Add(new TapGestureRecognizer { Command = vm.EditLineCommand }.Also(t => t.SetBinding(TapGestureRecognizer.CommandParameterProperty, ".")));

        var g = new Grid
        {
            ColumnSpacing = 6,
            Padding = new Thickness(0, 6),
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) },
        };
        g.Add(info, 0, 0);
        g.Add(Small("−", nameof(PosViewModel.DecLineCommand)), 1, 0);
        g.Add(Small("+", nameof(PosViewModel.IncLineCommand)), 2, 0);
        g.Add(amount, 3, 0);
        return g;
    });
}

internal static class ObjectExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}

/// <summary>FR-14 UPI: shows the shop's UPI QR with the amount; the shopkeeper taps "paid" after checking their UPI app.</summary>
public sealed class UpiPayPage : ContentPage, IQueryAttributable
{
    private readonly ShellNavigationService _nav;
    private readonly Image _qr = new() { HeightRequest = 280, WidthRequest = 280, HorizontalOptions = LayoutOptions.Center };
    private readonly Label _amount = Ui.Amount();
    private bool _answered;

    public UpiPayPage(ShellNavigationService nav, ILocalizer text)
    {
        _nav = nav;
        Title = text["tender.upi"];
        _amount.HorizontalTextAlignment = TextAlignment.Center;
        var paid = Ui.Button(text["pos.upi.paid"], Theme.Success, big: true);
        paid.Clicked += SafeUi.On(async () => { _answered = true; await _nav.ReturnAsync<string>("paid"); });
        var cancel = Ui.Secondary(text["common.cancel"]);
        cancel.Clicked += SafeUi.On(async () => { _answered = true; await _nav.ReturnAsync<string>(null); });
        Content = Ui.Scroll(Ui.Title(text["tender.upi.scan"]), _qr, _amount, paid, cancel);
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("amount", out var amount)) _amount.Text = amount?.ToString();
        if (query.TryGetValue("payload", out var payload) && payload is string upi)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(upi, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(12);
            _qr.Source = ImageSource.FromStream(() => new MemoryStream(png));
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_answered) { _answered = true; _nav.CancelPending(); }   // back button = not paid
    }
}

/// <summary>After finalize: bill number, total, change due, share on WhatsApp, new sale.</summary>
public sealed class ReceiptPage : ContentPage, IQueryAttributable
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly Label _number = Ui.Title("");
    private readonly Label _total = Ui.Amount();
    private readonly Label _change = Ui.Body();
    private Guid _billId;

    public ReceiptPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["receipt.title"];
        _total.HorizontalTextAlignment = TextAlignment.Center;
        _number.HorizontalTextAlignment = TextAlignment.Center;
        _change.HorizontalTextAlignment = TextAlignment.Center;
        var share = Ui.Button(text["receipt.share"], Theme.Success, big: true);
        share.Clicked += SafeUi.On(async () => await ShareAsync());
        var next = Ui.Secondary(text["pos.newbill"]);
        next.Clicked += SafeUi.On(async () => await Shell.Current.GoToAsync("//" + Routes.Pos));
        Content = Ui.Scroll(new Label { Text = "✅", FontSize = 56, HorizontalTextAlignment = TextAlignment.Center }, _number, _total, _change, share, next);
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("billId", out var id) && Guid.TryParse(id?.ToString(), out var billId)) _billId = billId;
        if (query.TryGetValue("change", out var change) && change is string c && c != IndianFormat.Rupees(default))
            _change.Text = _text.Format("pos.change.due", c);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var bill = await _sender.Send(new Angadi.Application.Catalog.GetBillQuery(_billId));
        if (bill is null) return;
        _number.Text = _text["receipt.bill"] + " " + bill.Number;
        _total.Text = IndianFormat.Rupees(bill.GrandTotal);
    }

    private async Task ShareAsync()
    {
        var r = await _sender.Send(new ShareReceiptCommand(_billId));
        if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
    }
}
