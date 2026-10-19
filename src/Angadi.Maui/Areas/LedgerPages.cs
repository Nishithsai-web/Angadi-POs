using System.Collections.ObjectModel;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Insights;
using Angadi.Application.Platform;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Customers;
using Angadi.Maui.Areas.Pos;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

public sealed record SimpleRow(string Id, string Title, string Detail, string Value, Color ValueColor);

internal static class Lists
{
    public static CollectionView Make(ObservableCollection<SimpleRow> rows, string empty, Func<SimpleRow, Task>? onTap = null)
    {
        var list = new CollectionView { ItemsSource = rows, SelectionMode = onTap is null ? SelectionMode.None : SelectionMode.Single, EmptyView = Ui.Caption(empty) };
        list.ItemTemplate = new DataTemplate(() =>
        {
            var title = new Label { FontFamily = Theme.FontBold, FontSize = 16 };
            title.SetBinding(Label.TextProperty, nameof(SimpleRow.Title));
            var detail = Ui.Caption();
            detail.SetBinding(Label.TextProperty, nameof(SimpleRow.Detail));
            var value = new Label { FontFamily = Theme.FontBold, HorizontalTextAlignment = TextAlignment.End, VerticalTextAlignment = TextAlignment.Center };
            value.SetBinding(Label.TextProperty, nameof(SimpleRow.Value));
            value.SetBinding(Label.TextColorProperty, nameof(SimpleRow.ValueColor));
            var g = new Grid { Padding = new Thickness(4, 8), ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
            g.Add(new VerticalStackLayout { Children = { title, detail } }, 0, 0);
            g.Add(value, 1, 0);
            return g;
        });
        if (onTap is not null)
            list.SelectionChanged += async (_, e) =>
            {
                if (e.CurrentSelection.FirstOrDefault() is SimpleRow row) await onTap(row);
                list.SelectedItem = null;
            };
        return list;
    }
}

/// <summary>FR-20: bills by day, tap for detail.</summary>
public sealed class BillsPage : ContentPage
{
    private readonly ISender _sender;
    private readonly ILocalizer _text;
    private readonly IClock _clock;
    private readonly ObservableCollection<SimpleRow> _rows = [];
    private readonly DatePicker _day = new() { Format = "dd MMM yyyy" };

    public BillsPage(ISender sender, ILocalizer text, IClock clock)
    {
        _sender = sender;
        _text = text;
        _clock = clock;
        Title = text["nav.bills"];
        _day.DateSelected += async (_, _) => await LoadAsync();
        var list = Lists.Make(_rows, text["common.empty"], row => Shell.Current.GoToAsync(Routes.BillDetail, new Dictionary<string, object> { ["billId"] = row.Id }));
        var grid = new Grid { Padding = 12, RowSpacing = 8, RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
        grid.Add(_day, 0, 0);
        grid.Add(list, 0, 1);
        Content = grid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_day.Date == DateTime.MinValue || _rows.Count == 0) _day.Date = _clock.Today.ToDateTime(TimeOnly.MinValue);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var day = DateOnly.FromDateTime(_day.Date);
        var bills = await _sender.Send(new ListBillsQuery(day, day));
        _rows.Clear();
        foreach (var b in bills)
            _rows.Add(new SimpleRow(b.BillId.ToString("D"), b.Number,
                $"{b.FinalizedAt:HH:mm} · {_text["tender." + b.Tender.ToString().ToLowerInvariant()]}" + (b.Status == BillStatus.Void ? " · " + _text["receipt.void"] : ""),
                IndianFormat.Rupees(b.GrandTotal), b.Status == BillStatus.Void ? Theme.Danger : Theme.TextPrimary));
    }
}

/// <summary>FR-16/19/20: bill lines and totals, share again, void with reason.</summary>
public sealed class BillDetailPage : ContentPage, IQueryAttributable
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly VerticalStackLayout _body = new() { Spacing = 6, Padding = 16 };
    private Guid _billId;

    public BillDetailPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["nav.bills"];
        Content = new ScrollView { Content = _body };
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("billId", out var id) && Guid.TryParse(id?.ToString(), out var billId)) { _billId = billId; _ = LoadAsync(); }
    }

    private async Task LoadAsync()
    {
        var bill = await _sender.Send(new GetBillQuery(_billId));
        _body.Children.Clear();
        if (bill is null) return;
        _body.Add(Ui.Title(bill.Number + (bill.Status == BillStatus.Void ? " · " + _text["receipt.void"] : "")));
        _body.Add(Ui.Caption($"{bill.FinalizedAt:dd MMM yyyy HH:mm} · {_text["bill." + (bill.IsTaxInvoice ? "taxinvoice" : "billofsupply")]}"));
        foreach (var l in bill.Lines)
            _body.Add(Line($"{(string.IsNullOrWhiteSpace(l.TeluguName) ? l.EnglishName : l.TeluguName)}  {IndianFormat.Quantity(l.Qty)} {_text.UnitLabel(l.Unit)}", IndianFormat.Rupees(l.LineTotal)));
        _body.Add(new BoxView { HeightRequest = 1, Color = Theme.Border });
        _body.Add(Line(_text["bill.subtotal"], IndianFormat.Rupees(bill.SubTotal)));
        _body.Add(Line(_text["bill.discount"], IndianFormat.Rupees(BillCalculator.TotalDiscount(bill))));
        foreach (var s in bill.TaxSplits)
            _body.Add(Line($"GST {(int)s.Slab / 100}% ({_text["receipt.taxable"]} {IndianFormat.Rupees(s.Taxable)})", IndianFormat.Rupees(s.Tax)));
        if (bill.RoundingAdjustment.Value != 0) _body.Add(Line(_text["bill.rounding"], IndianFormat.Rupees(bill.RoundingAdjustment)));
        var total = Ui.Amount(IndianFormat.Rupees(bill.GrandTotal));
        _body.Add(total);

        var share = Ui.Button(_text["bills.reshare"], Theme.Success);
        share.Clicked += async (_, _) =>
        {
            var r = await _sender.Send(new ShareReceiptCommand(_billId));
            if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
        };
        _body.Add(share);

        if (bill.Status == BillStatus.Finalized)
        {
            var voidBtn = Ui.Button(_text["bill.void"], Theme.Danger);
            voidBtn.Clicked += async (_, _) => await VoidAsync();
            _body.Add(voidBtn);
        }
    }

    private static Grid Line(string label, string value)
    {
        var g = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        g.Add(Ui.Body(label), 0, 0);
        g.Add(new Label { Text = value, HorizontalTextAlignment = TextAlignment.End }, 1, 0);
        return g;
    }

    private async Task VoidAsync()
    {
        var reason = await _dialogs.PromptAsync(_text["bill.void"], _text["bill.void.reason"], _text["common.ok"], _text["common.cancel"]);
        if (string.IsNullOrWhiteSpace(reason)) return;
        var r = await _sender.Send(new VoidBillCommand(_billId, reason));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        await LoadAsync();
    }
}

/// <summary>FR-21/30: customers with dues; tap for ledger, payment and reminder.</summary>
public sealed class CustomersPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly ObservableCollection<SimpleRow> _rows = [];
    private readonly Label _total = Ui.Amount();
    private readonly Entry _search;

    public CustomersPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["nav.customers"];
        _search = Ui.Entry(text["customers.name"]);
        _search.TextChanged += async (_, _) => await LoadAsync();
        var add = Ui.Button("+ " + text["customers.add"]);
        add.Clicked += async (_, _) => await AddAsync();
        var top = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        top.Add(_search, 0, 0);
        top.Add(add, 1, 0);
        var list = Lists.Make(_rows, text["common.empty"], row => Shell.Current.GoToAsync(Routes.CustomerDetail, new Dictionary<string, object> { ["customerId"] = row.Id }));
        var grid = new Grid { Padding = 12, RowSpacing = 8, RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) } };
        grid.Add(top, 0, 0);
        grid.Add(Ui.Caption(text["reports.dues"]), 0, 1);
        grid.Add(_total, 0, 2);
        grid.Add(list, 0, 3);
        Content = grid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var dues = await _sender.Send(new GetDuesOutstandingQuery());
        _total.Text = IndianFormat.Rupees(dues.Total);
        var customers = await _sender.Send(new SearchCustomersQuery(_search.Text ?? ""));
        _rows.Clear();
        foreach (var c in customers.OrderByDescending(c => c.DuesBalance.Value))
            _rows.Add(new SimpleRow(c.Id.ToString("D"), c.Name, Mask(c.Phone), IndianFormat.Rupees(c.DuesBalance),
                c.DuesBalance.Value > 0 ? Theme.UdhaarColor : Theme.TextSecondary));
    }

    private static string Mask(string? phone) => string.IsNullOrEmpty(phone) || phone.Length < 4 ? "" : "••••••" + phone[^4..];

    private async Task AddAsync()
    {
        var name = await _dialogs.PromptAsync(_text["customers.add"], _text["customers.name"], _text["common.ok"], _text["common.cancel"]);
        if (string.IsNullOrWhiteSpace(name)) return;
        var phone = await _dialogs.PromptAsync(_text["customers.add"], _text["customers.phone"], _text["common.ok"], _text["common.cancel"], keyboard: Keyboard.Telephone);
        var r = await _sender.Send(new SaveCustomerCommand(new Customer { Name = name, Phone = phone }));
        if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
        await LoadAsync();
    }
}

/// <summary>FR-22/23: ledger, record payment, WhatsApp reminder.</summary>
public sealed class CustomerDetailPage : ContentPage, IQueryAttributable
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly ObservableCollection<SimpleRow> _rows = [];
    private readonly Label _balance = Ui.Amount();
    private Guid _customerId;

    public CustomerDetailPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["customers.ledger"];
        var pay = Ui.Button(text["customers.pay"], Theme.Success);
        pay.Clicked += async (_, _) => await PayAsync();
        var remind = Ui.Secondary(text["customers.remind"]);
        remind.Clicked += async (_, _) =>
        {
            var r = await _sender.Send(new SendDuesReminderCommand(_customerId));
            if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
        };
        var actions = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        actions.Add(pay, 0, 0);
        actions.Add(remind, 1, 0);
        var grid = new Grid { Padding = 12, RowSpacing = 8, RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) } };
        grid.Add(_balance, 0, 0);
        grid.Add(actions, 0, 1);
        grid.Add(Lists.Make(_rows, text["common.empty"]), 0, 2);
        Content = grid;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("customerId", out var id) && Guid.TryParse(id?.ToString(), out var cid)) { _customerId = cid; _ = LoadAsync(); }
    }

    private async Task LoadAsync()
    {
        var c = await _sender.Send(new GetCustomerQuery(_customerId));
        if (c is null) return;
        Title = c.Name;
        _balance.Text = IndianFormat.Rupees(c.DuesBalance);
        _rows.Clear();
        foreach (var e in await _sender.Send(new GetCustomerLedgerQuery(_customerId)))
        {
            var (label, color, sign) = e.Kind switch
            {
                DuesEntryKind.Charge => (_text["customers.charge"], Theme.UdhaarColor, "+"),
                DuesEntryKind.Payment => (_text["customers.payment"], Theme.Success, "−"),
                _ => (_text["customers.reversal"], Theme.TextSecondary, "−"),
            };
            _rows.Add(new SimpleRow(e.Id.ToString("D"), label, $"{e.CreatedAt:dd MMM HH:mm}", sign + IndianFormat.Rupees(e.Amount), color));
        }
    }

    private async Task PayAsync()
    {
        var value = await _dialogs.PromptAsync(_text["customers.pay"], _text["pos.amount.prompt"], _text["common.ok"], _text["common.cancel"], keyboard: Keyboard.Numeric);
        if (value is null || !PosInput.TryParseRupees(value, out var amount) || amount.Value <= 0) return;
        var r = await _sender.Send(new RecordDuesPaymentCommand(_customerId, amount, null));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        await LoadAsync();
    }
}

/// <summary>FR-27–33: day-end, GST, top items, and on-device insights.</summary>
public sealed class ReportsPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IInsightsService _insights;
    private readonly ILocalizer _text;
    private readonly IClock _clock;
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 16 };
    private readonly DatePicker _day = new() { Format = "dd MMM yyyy" };

    public ReportsPage(ISender sender, IInsightsService insights, ILocalizer text, IClock clock)
    {
        _sender = sender;
        _insights = insights;
        _text = text;
        _clock = clock;
        Title = text["nav.reports"];
        _day.DateSelected += async (_, _) => await LoadAsync();
        Content = new ScrollView { Content = new VerticalStackLayout { Children = { new ContentView { Padding = new Thickness(16, 8, 16, 0), Content = _day }, _body } } };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_day.Date == DateTime.MinValue || _body.Children.Count == 0) _day.Date = _clock.Today.ToDateTime(TimeOnly.MinValue);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var day = DateOnly.FromDateTime(_day.Date);
        var r = await _sender.Send(new GetDayEndReportQuery(day));
        var month = new DateOnly(day.Year, day.Month, 1);
        var gst = await _sender.Send(new GetGstSummaryQuery(month, day));
        var top = await _sender.Send(new GetTopItemsQuery(day.AddDays(-6), day, 5));
        var morning = await _insights.GetMorningSummaryAsync();
        var reorder = await _insights.GetReorderForecastAsync();
        var dead = await _insights.GetDeadStockAsync(30);

        _body.Children.Clear();
        _body.Add(Card(_text["reports.dayend"],
            (_text["reports.sales"], IndianFormat.Rupees(r.SalesTotal)),
            (_text["reports.bills"], r.BillCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (_text["reports.cash"], IndianFormat.Rupees(r.Cash)),
            (_text["reports.upi"], IndianFormat.Rupees(r.Upi)),
            (_text["reports.udhaar"], IndianFormat.Rupees(r.Udhaar)),
            (_text["bill.discount"], IndianFormat.Rupees(r.Discounts)),
            (_text["bill.tax"], IndianFormat.Rupees(r.Tax)),
            (_text["reports.voids"], r.VoidCount.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        _body.Add(Card(_text["reports.gst"], gst.Select(s => ($"GST {(int)s.Slab / 100}% · {_text["receipt.taxable"]} {IndianFormat.Rupees(s.Taxable)}", IndianFormat.Rupees(s.Tax))).ToArray()));
        _body.Add(Card(_text["reports.top"], top.Select(t => ($"{t.DisplayName} · {IndianFormat.Quantity(t.QtySold)} {_text.UnitLabel(t.Unit)}", IndianFormat.Rupees(t.Value))).ToArray()));
        _body.Add(Card(_text["insights.title"],
            (_text.Format("insights.morning", IndianFormat.Rupees(morning.YesterdaySales), IndianFormat.Rupees(morning.SevenDayAverage)), ""),
            (morning.TopMoverName is null ? "" : _text.Format("insights.topmover", morning.TopMoverName), ""),
            (_text.Format("insights.collect", IndianFormat.Rupees(morning.DuesToCollect)), ""),
            (_text["stock.low.title"], morning.LowStockCount.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        _body.Add(Card(_text["insights.reorder.title"], reorder.Select(f => (_text.Format("insights.reorder", f.DisplayName, f.DaysLeft), "")).ToArray()));
        _body.Add(Card(_text.Format("insights.dead", 30), dead.Select(d => (d.DisplayName, IndianFormat.Rupees(d.StockValue))).ToArray()));
        _body.Add(Ui.Caption(_text["insights.ondevice"]));
    }

    private View Card(string title, params (string Label, string Value)[] rows)
    {
        var stack = new VerticalStackLayout { Spacing = 4, Children = { new Label { Text = title, FontFamily = Theme.FontBold, FontSize = 18 } } };
        if (rows.Length == 0) stack.Add(Ui.Caption(_text["common.empty"]));
        foreach (var (label, value) in rows.Where(r => r.Label.Length > 0))
        {
            var g = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
            g.Add(Ui.Body(label), 0, 0);
            g.Add(new Label { Text = value, FontFamily = Theme.FontBold, HorizontalTextAlignment = TextAlignment.End }, 1, 0);
            stack.Add(g);
        }
        return Ui.Card(stack);
    }
}
