using System.Collections.ObjectModel;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Insights;
using Angadi.Application.Inventory;
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
            list.SelectionChanged += SafeUi.On<SelectionChangedEventArgs>(async e =>
            {
                if (e.CurrentSelection.FirstOrDefault() is SimpleRow row) await onTap(row);
                list.SelectedItem = null;
            });
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
        _day.DateSelected += SafeUi.On<DateChangedEventArgs>(async _ => await LoadAsync());
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
        var day = DateOnly.FromDateTime((DateTime?)_day.Date ?? DateTime.Today);
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
            _body.Add(Line($"{_text.Name(l)}  {IndianFormat.Quantity(l.Qty)} {_text.UnitLabel(l.Unit)}", IndianFormat.Rupees(l.LineTotal)));
        _body.Add(new BoxView { HeightRequest = 1, Color = Theme.Border });
        _body.Add(Line(_text["bill.subtotal"], IndianFormat.Rupees(bill.SubTotal)));
        _body.Add(Line(_text["bill.discount"], IndianFormat.Rupees(BillCalculator.TotalDiscount(bill))));
        foreach (var s in bill.TaxSplits)
        {
            _body.Add(Line($"{_text["bill.tax"]} {Rate((int)s.Slab)} ({_text["receipt.taxable"]} {IndianFormat.Rupees(s.Taxable)})", IndianFormat.Rupees(s.Tax)));
            if (s.Tax.Value == 0) continue;
            _body.Add(Line($"   {_text["receipt.cgst"]} {Rate(s.HalfRateBasisPoints)}", IndianFormat.Rupees(s.Cgst)));   // A2-02
            _body.Add(Line($"   {_text["receipt.sgst"]} {Rate(s.HalfRateBasisPoints)}", IndianFormat.Rupees(s.Sgst)));
        }
        if (bill.RoundingAdjustment.Value != 0) _body.Add(Line(_text["bill.rounding"], IndianFormat.Rupees(bill.RoundingAdjustment)));
        var total = Ui.Amount(IndianFormat.Rupees(bill.GrandTotal));
        _body.Add(total);

        var share = Ui.Button(_text["bills.reshare"], Theme.Success);
        share.Clicked += SafeUi.On(async () =>
        {
            var r = await _sender.Send(new ShareReceiptCommand(_billId));
            if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
        });
        _body.Add(share);

        if (bill.Status == BillStatus.Finalized)
        {
            var voidBtn = Ui.Button(_text["bill.void"], Theme.Danger);
            voidBtn.Clicked += SafeUi.On(async () => await VoidAsync());
            _body.Add(voidBtn);
        }
    }

    /// <summary>500 → "5%", 250 → "2.5%".</summary>
    internal static string Rate(int basisPoints) =>
        (basisPoints / 100m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";

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
        // A2-14: a bill from an earlier month changes that month's (maybe filed) GST summary: ask, then confirm.
        if (r.IsFailure && r.Error.Code == ErrorCodes.BillVoidPastMonth)
        {
            if (!await _dialogs.ConfirmAsync(_text["bill.void"], _text["bill.void.pastmonth"], _text["common.ok"], _text["common.cancel"])) return;
            r = await _sender.Send(new VoidBillCommand(_billId, reason, ConfirmPastMonth: true));
        }
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
        _day.DateSelected += SafeUi.On<DateChangedEventArgs>(async _ => await LoadAsync());
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
        var day = DateOnly.FromDateTime((DateTime?)_day.Date ?? DateTime.Today);
        var r = await _sender.Send(new GetDayEndReportQuery(day));
        var month = new DateOnly(day.Year, day.Month, 1);
        var gst = await _sender.Send(new GetGstSummaryQuery(month, day));
        var top = await _sender.Send(new GetTopItemsQuery(day.AddDays(-6), day, 5));
        var morning = await _insights.GetMorningSummaryAsync();
        var reorder = await _insights.GetReorderForecastAsync();
        var dead = await _insights.GetDeadStockAsync(30);
        var inventory = await _sender.Send(new GetInventoryReportQuery());
        var moves = await _sender.Send(new GetStockMovesQuery(day, day));

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
        // Inventory: what is on the shelf, what it is worth, and whether every change was recorded.
        _body.Add(Card(_text["inventory.title"],
            (_text["inventory.value"], IndianFormat.Rupees(inventory.StockValue)),
            (_text["inventory.items"], inventory.ItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (_text["inventory.low"], inventory.LowCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (_text["inventory.out"], inventory.OutCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (inventory.LedgerMatches ? _text["inventory.ledger.ok"] : _text.Format("inventory.ledger.bad", inventory.Mismatches.Count), "")));
        _body.Add(Card(_text["inventory.moves"], moves.Take(15).Select(m => (
            $"{m.Name} · {_text["stock.move." + m.Reason]}",
            (m.Delta.Milli > 0 ? "+" : "") + IndianFormat.Quantity(m.Delta) + " " + _text.UnitLabel(m.Unit))).ToArray()));
        // A2-02: each slab with its CGST and SGST halves (intra-state), as the shop files them.
        _body.Add(Card(_text["reports.gst"], gst.SelectMany(s => s.Tax.Value == 0
            ? new[] { ($"{_text["bill.tax"]} {BillDetailPage.Rate((int)s.Slab)} · {_text["receipt.taxable"]} {IndianFormat.Rupees(s.Taxable)}", IndianFormat.Rupees(s.Tax)) }
            : new[]
            {
                ($"{_text["bill.tax"]} {BillDetailPage.Rate((int)s.Slab)} · {_text["receipt.taxable"]} {IndianFormat.Rupees(s.Taxable)}", IndianFormat.Rupees(s.Tax)),
                ($"   {_text["receipt.cgst"]} {BillDetailPage.Rate(s.HalfRateBasisPoints)}", IndianFormat.Rupees(s.Cgst)),
                ($"   {_text["receipt.sgst"]} {BillDetailPage.Rate(s.HalfRateBasisPoints)}", IndianFormat.Rupees(s.Sgst)),
            }).ToArray()));
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
