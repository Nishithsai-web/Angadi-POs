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
    private readonly ScrollView _leftScroll = new();
    private CollectionView? _results;
    private CollectionView? _quick;
    private Entry? _searchEntry;
    private View? _totals;
    private bool? _wide;

    public PosPage(PosViewModel vm) : base(vm)
    {
        Title = vm.Text["nav.pos"];
        _left = BuildCatalogue(vm);
        _right = BuildBill(vm);
        Content = _root;
        // VOICE-9 (FIX-1, 2026-10-20): the voice "manual" fallback raises this so typed search is
        // actually in front of the shopkeeper — focus the search box, keyboard up.
        vm.SearchFocusRequested += (_, _) => Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => _searchEntry?.Focus());
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        var wide = width >= 900;
        if (_wide == wide) return;
        _wide = wide;
        if (_quick is not null) _quick.HeightRequest = wide ? -1 : 196;            // 2 full rows of tiles on a phone
        if (_results is not null) _results.MaximumHeightRequest = wide ? 240 : 150;
        if (_totals is not null) _totals.IsVisible = wide;                          // phone: the total alone
        _root.Children.Clear();
        _leftScroll.Content = null;
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
            // Phone: the bill ALWAYS gets 58% of the height (its lines must never be squeezed out, whatever the
            // catalogue holds); the catalogue scrolls inside the other 42%.
            _root.RowDefinitions.Add(new RowDefinition(new GridLength(0.42, GridUnitType.Star)));
            _root.RowDefinitions.Add(new RowDefinition(new GridLength(0.58, GridUnitType.Star)));
            _leftScroll.Content = _left;
            _root.Add(_leftScroll, 0, 0);
            _root.Add(_right, 0, 1);
        }
    }

    private View BuildCatalogue(PosViewModel vm)
    {
        var mic = new Button
        {
            Text = "🎤  " + vm.Text["pos.mic"], FontSize = 20, MinimumHeightRequest = 72, BackgroundColor = Theme.Accent, TextColor = Theme.TextPrimary,
        };
        mic.SetBinding(Button.CommandProperty, nameof(PosViewModel.MicCommand));
        var scan = Ui.Secondary("▦  " + vm.Text["pos.scan"]);
        scan.SetBinding(Button.CommandProperty, nameof(PosViewModel.ScanCommand));

        var dueBanner = new Button { BackgroundColor = Color.FromArgb("#FDF1E3"), TextColor = Theme.UdhaarColor, FontSize = 15, CornerRadius = 10 };
        dueBanner.SetBinding(Button.TextProperty, nameof(PosViewModel.DueBanner));
        dueBanner.SetBinding(Button.CommandProperty, nameof(PosViewModel.OpenDueCommand));
        dueBanner.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(PosViewModel.DueBanner), converter: new NotEmpty()));
        var voiceText = Ui.Caption();
        voiceText.SetBinding(Label.TextProperty, nameof(PosViewModel.VoiceText));

        // A2-03: no manual backup for a week → tap to back up (Settings); ✕ hides it until tomorrow.
        var backupNow = new Button
        {
            BackgroundColor = Color.FromArgb("#FFF4D6"), TextColor = Theme.Warning, FontSize = 15, CornerRadius = 10, LineBreakMode = LineBreakMode.WordWrap,
        };
        backupNow.SetBinding(Button.TextProperty, nameof(PosViewModel.BackupBanner));
        backupNow.SetBinding(Button.CommandProperty, nameof(PosViewModel.BackupNowCommand));
        var hideBackup = new Button { Text = "✕", WidthRequest = 48, BackgroundColor = Color.FromArgb("#FFF4D6"), TextColor = Theme.Warning, CornerRadius = 10, Padding = 0 };
        SemanticProperties.SetDescription(hideBackup, vm.Text["backup.reminder.hide"]);
        hideBackup.SetBinding(Button.CommandProperty, nameof(PosViewModel.HideBackupBannerCommand));
        var backupBanner = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 6 };
        backupBanner.Add(backupNow, 0, 0);
        backupBanner.Add(hideBackup, 1, 0);
        backupBanner.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(PosViewModel.BackupBanner), converter: new NotEmpty()));

        var search = _searchEntry = Ui.Entry(vm.Text["pos.search"]);
        search.SetBinding(Entry.TextProperty, nameof(PosViewModel.SearchText));

        var results = _results = new CollectionView { SelectionMode = SelectionMode.Single, ItemTemplate = TileTemplate(), MaximumHeightRequest = 240 };
        // No results, no space: the list only exists while a search has found something.
        results.SetBinding(VisualElement.IsVisibleProperty, new Binding("SearchResults.Count", converter: new Positive()));
        results.SetBinding(ItemsView.ItemsSourceProperty, nameof(PosViewModel.SearchResults));
        results.SelectionChanged += SafeUi.On<SelectionChangedEventArgs>(e => Pick(vm, results, e));   // A1-03

        var quick = _quick = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = TileTemplate(),
            ItemsLayout = new GridItemsLayout(3, ItemsLayoutOrientation.Vertical) { HorizontalItemSpacing = 8, VerticalItemSpacing = 8 },
        };
        quick.SetBinding(ItemsView.ItemsSourceProperty, nameof(PosViewModel.QuickSale));
        quick.SelectionChanged += SafeUi.On<SelectionChangedEventArgs>(e => Pick(vm, quick, e, quickSale: true));

        var actions = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        actions.Add(mic, 0, 0);
        actions.Add(scan, 1, 0);

        var grid = new Grid
        {
            RowSpacing = 8,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
        };
        grid.Add(backupBanner, 0, 0);
        grid.Add(dueBanner, 0, 1);
        grid.Add(actions, 0, 2);
        grid.Add(voiceText, 0, 3);
        grid.Add(search, 0, 4);
        grid.Add(results, 0, 5);
        grid.Add(Ui.Caption(vm.Text["pos.quicksale"]), 0, 6);
        grid.Add(quick, 0, 7);
        return grid;
    }

    private static Task Pick(PosViewModel vm, CollectionView list, SelectionChangedEventArgs e, bool quickSale = false)
    {
        try
        {
            if (e.CurrentSelection.FirstOrDefault() is ItemTile tile)
                vm.Add(tile, quickSale ? LineSource.QuickSale : LineSource.Manual);
        }
        finally { list.SelectedItem = null; }   // a tile must stay tappable again even after a failure
        return Task.CompletedTask;
    }

    private static DataTemplate TileTemplate() => new(() =>
    {
        var name = new Label { FontFamily = Theme.FontBold, FontSize = 16, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(ItemTile.Name));
        var price = Ui.Caption();
        price.SetBinding(Label.TextProperty, nameof(ItemTile.Price));
        return Ui.Card(new VerticalStackLayout { Spacing = 2, Children = { name, price } }, 10);
    });

    private View BuildBill(PosViewModel vm)
    {
        // The bill lines always keep room: on a phone the other rows used to squeeze them to zero height,
        // so items were added but invisible (found by the CI emulator test).
        var lines = new CollectionView { ItemTemplate = LineTemplate(vm), SelectionMode = SelectionMode.None, MinimumHeightRequest = 150 };
        lines.SetBinding(ItemsView.ItemsSourceProperty, nameof(PosViewModel.Lines));
        lines.EmptyView = Ui.StateLine(nameof(PosViewModel.EmptyText));

        var totals = _totals = new VerticalStackLayout
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

internal sealed class Positive : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is int n && n > 0;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

internal sealed class NotEmpty : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => !string.IsNullOrEmpty(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
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
    private readonly ISender _sender;
    private readonly ILocalizer _text;
    private readonly Image _qr = new() { HeightRequest = 280, WidthRequest = 280, HorizontalOptions = LayoutOptions.Center };
    private readonly Label _amount = Ui.Amount();
    private readonly Label _note = Ui.Caption();
    private readonly Label _payee = Ui.Body();
    private readonly Button _switch;
    private ImageSource? _generated;
    private ImageSource? _ownPhoto;
    private bool _showingOwn;
    private bool _answered;

    public UpiPayPage(ShellNavigationService nav, ISender sender, ILocalizer text)
    {
        _nav = nav;
        _sender = sender;
        _text = text;
        Title = text["tender.upi"];
        _amount.HorizontalTextAlignment = TextAlignment.Center;
        _note.HorizontalTextAlignment = TextAlignment.Center;
        _payee.HorizontalTextAlignment = TextAlignment.Center;
        // A2-06: the common counter fraud is a fake "payment successful" screen on the customer's phone.
        var checkOwn = new Label
        {
            Text = text["upi.check.own"], FontFamily = Theme.FontBold, FontSize = 16, TextColor = Theme.Warning,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        // The owner's uploaded QR photo (if any) can be shown instead; the generated one carries the amount.
        _switch = Ui.Secondary(text["upi.show.mine"]);
        _switch.IsVisible = false;
        _switch.Clicked += (_, _) => Show(!_showingOwn);
        var paid = Ui.Button(text["pos.upi.paid"], Theme.Success, big: true);
        paid.Clicked += SafeUi.On(async () => { _answered = true; await _nav.ReturnAsync<string>("paid"); });
        var cancel = Ui.Secondary(text["common.cancel"]);
        cancel.Clicked += SafeUi.On(async () => { _answered = true; await _nav.ReturnAsync<string>(null); });
        Content = Ui.Scroll(Ui.Title(text["tender.upi.scan"]), _qr, _amount, _payee, _note, checkOwn, _switch, paid, cancel);
    }

    private void Show(bool own)
    {
        _showingOwn = own && _ownPhoto is not null;
        _qr.Source = _showingOwn ? _ownPhoto : _generated;
        _switch.Text = _showingOwn ? _text["upi.show.amount"] : _text["upi.show.mine"];
        _note.Text = _showingOwn ? _text.Format("upi.mine.note", _amount.Text ?? "") : "";
    }

    private async Task LoadOwnPhotoAsync()
    {
        try
        {
            var shop = await _sender.Send(new Angadi.Application.Catalog.GetShopProfileQuery());
            if (shop?.UpiQrImage is not { Length: > 0 } bytes) return;
            _ownPhoto = ImageSource.FromStream(() => new MemoryStream(bytes));
            _switch.IsVisible = true;
        }
        catch (Exception ex) { GlobalExceptions.Report(ex, "UpiPayPage.photo"); }
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("amount", out var amount)) _amount.Text = amount?.ToString();
        if (query.TryGetValue("payload", out var payload) && payload is string upi)
        {
            _payee.Text = UpiQr.Parse(upi) is { } payee ? _text.Format("upi.paying.to", payee.PayeeAddress) : "";
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(upi, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(12);
            _generated = ImageSource.FromStream(() => new MemoryStream(png));
            Show(false);
        }
        _ = LoadOwnPhotoAsync();
    }

    /// <summary>
    /// Back = not paid. On navigation only, never OnDisappearing: the shopkeeper switching to PhonePe/GPay to check
    /// the money arrived must not cancel the pending payment ("Payment received" afterwards would do nothing).
    /// </summary>
    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        if (!_answered) { _answered = true; _nav.CancelPending(); }
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
    private readonly Button _toCustomer;
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
        // Bill for a registered customer: send it (and their udhaar balance) to THEIR WhatsApp number.
        _toCustomer = Ui.Button("", Theme.Success, big: true);
        _toCustomer.IsVisible = false;
        _toCustomer.Clicked += SafeUi.On(async () =>
        {
            var r = await _sender.Send(new SendBillToCustomerCommand(_billId));
            if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
        });
        var next = Ui.Secondary(text["pos.newbill"]);
        next.Clicked += SafeUi.On(async () => await Shell.Current.GoToAsync("//" + Routes.Pos));
        Content = Ui.Scroll(new Label { Text = "✅", FontSize = 56, HorizontalTextAlignment = TextAlignment.Center }, _number, _total, _change, _toCustomer, share, next);
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("billId", out var id) && Guid.TryParse(id?.ToString(), out var billId)) _billId = billId;
        if (query.TryGetValue("change", out var change) && change is string c && c != IndianFormat.Rupees(default))
            _change.Text = _text.Format("pos.change.due", c);
        _ = SafeUi.Run(LoadAsync);   // A1-03
    }

    private async Task LoadAsync()
    {
        var bill = await _sender.Send(new Angadi.Application.Catalog.GetBillQuery(_billId));
        if (bill is null) return;
        _number.Text = _text["receipt.bill"] + " " + bill.Number;
        _total.Text = IndianFormat.Rupees(bill.GrandTotal);
        var customer = bill.CustomerId is { } cid ? await _sender.Send(new Angadi.Application.Catalog.GetCustomerQuery(cid)) : null;
        _toCustomer.IsVisible = customer is { Phone: { Length: > 0 } };
        if (customer is not null) _toCustomer.Text = _text.Format("receipt.send.customer", customer.Name);
    }

    private async Task ShareAsync()
    {
        var r = await _sender.Send(new ShareReceiptCommand(_billId));
        if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
    }
}
