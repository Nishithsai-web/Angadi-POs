using System.Collections.ObjectModel;
using System.Globalization;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Customers;
using Angadi.Application.Platform;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Customers;
using Angadi.Maui.Areas.Pos;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

internal static class CustomerFormat
{
    /// <summary>919876543210 → 98765 43210 (the owner's own device: the full number is fine to show).</summary>
    public static string Phone(string? e164) =>
        e164 is { Length: 12 } p && p.StartsWith("91", StringComparison.Ordinal) ? $"{p[2..7]} {p[7..]}" : e164 ?? "";

    public static string Day(DateTimeOffset? t) => t is { } v ? v.ToLocalTime().ToString("dd MMM yyyy", CultureInfo.InvariantCulture) : "—";

    public static string TenderLabel(ILocalizer text, TenderType t) => t switch
    {
        TenderType.Cash => text["tender.cash"],
        TenderType.Upi => text["tender.upi"],
        _ => text["tender.udhaar"],
    };
}

/// <summary>FR-21/23: customer list, total dues, and today's "due for reminder" list with one-tap WhatsApp/SMS.</summary>
public sealed class CustomersPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly ObservableCollection<SimpleRow> _rows = [];
    private readonly Label _total = Ui.Amount();
    private readonly Entry _search;
    private readonly VerticalStackLayout _due = new() { Spacing = 6 };

    public CustomersPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["nav.customers"];
        _search = Ui.Entry(text["customers.search"]);
        _search.TextChanged += SafeUi.On<TextChangedEventArgs>(async _ => await LoadListAsync(), singleFlight: false);
        var add = Ui.Button("+ " + text["customers.add"]);
        add.Clicked += SafeUi.On(async () => await AddAsync());
        var top = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        top.Add(_search, 0, 0);
        top.Add(add, 1, 0);
        var list = Lists.Make(_rows, text["customers.empty"], row => Shell.Current.GoToAsync(Routes.CustomerDetail, new Dictionary<string, object> { ["customerId"] = row.Id }));

        var duesHeader = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        duesHeader.Add(Ui.Caption(text["customers.dues.total"]), 0, 0);
        duesHeader.Add(_total, 1, 0);

        var grid = new Grid { Padding = 12, RowSpacing = 8, RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) } };
        grid.Add(duesHeader, 0, 0);
        grid.Add(_due, 0, 1);
        grid.Add(top, 0, 2);
        grid.Add(list, 0, 3);
        Content = grid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // FIX(UI-1, 2026-10-20, FIX-3): Flaw: unwrapped async void OnAppearing on a ContentPage — a failed load
        // escaped SafeUi (no toast, half-loaded page). Fix: run the load through SafeUi.Run like the other 7
        // secondary pages. Why: "never-fail UI" must hold on every page. Logged in docs/AGENT_FIX_LOG.md.
        await SafeUi.Run(async () =>
        {
            await LoadDueAsync();
            await LoadListAsync();
        });
    }

    private async Task LoadDueAsync()
    {
        var dues = await _sender.Send(new GetDuesOutstandingQuery());
        _total.Text = IndianFormat.Rupees(dues.Total);
        var due = await _sender.Send(new GetDueRemindersQuery());
        _due.Children.Clear();
        if (due.Count == 0) return;

        var card = new VerticalStackLayout { Spacing = 6, Children = { new Label { Text = _text.Format("customers.due.title", due.Count), FontFamily = Theme.FontBold, FontSize = 17, TextColor = Theme.UdhaarColor } } };
        foreach (var d in due.Take(5))
        {
            var why = d.Reason == DueReason.PromiseDatePassed && d.PromisedPayDate is { } p
                ? _text.Format("customers.due.promised", _text.Short(p))
                : _text.Format("customers.due.old", d.OldestUnpaidDays);
            var info = new VerticalStackLayout { Children = { Ui.Body(d.Name + " · " + IndianFormat.Rupees(d.Balance)), Ui.Caption(why) } };
            info.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(SafeUi.Fire(() => Shell.Current.GoToAsync(Routes.CustomerDetail, new Dictionary<string, object> { ["customerId"] = d.CustomerId.ToString("D") }))),   // A1-03
            });
            var wa = Ui.Button("WhatsApp", Theme.Success);
            wa.Clicked += SafeUi.On(async () => await RemindAsync(d, ReminderChannel.WhatsApp));
            var sms = Ui.Secondary("SMS");
            sms.Clicked += SafeUi.On(async () => await RemindAsync(d, ReminderChannel.Sms));
            var row = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) }, ColumnSpacing = 6 };
            row.Add(info, 0, 0);
            row.Add(wa, 1, 0);
            row.Add(sms, 2, 0);
            card.Add(row);
        }
        _due.Add(Ui.Card(card, 10));
    }

    private async Task RemindAsync(DueReminder d, ReminderChannel channel)
    {
        var r = await _sender.Send(new SendDuesReminderCommand(d.CustomerId, channel));
        await _dialogs.ToastAsync(r.IsFailure ? _text.Error(r.Error) : _text.Format("customers.reminded", d.Name));
        await LoadDueAsync();
    }

    private async Task LoadListAsync()
    {
        var customers = await _sender.Send(new SearchCustomersQuery(_search.Text ?? ""));
        _rows.Clear();
        foreach (var c in customers.OrderByDescending(c => c.DuesBalance.Value).ThenBy(c => c.Name, StringComparer.Ordinal))
            _rows.Add(new SimpleRow(c.Id.ToString("D"), c.Name,
                string.IsNullOrEmpty(c.Phone) ? _text["customers.nophone"] : CustomerFormat.Phone(c.Phone),
                c.DuesBalance.Value > 0 ? IndianFormat.Rupees(c.DuesBalance) : _text["customers.nodues"],
                c.DuesBalance.Value > 0 ? Theme.UdhaarColor : Theme.TextSecondary));
    }

    private async Task AddAsync()
    {
        var name = await _dialogs.PromptAsync(_text["customers.add"], _text["customers.name"], _text["common.ok"], _text["common.cancel"]);
        if (string.IsNullOrWhiteSpace(name)) return;
        var phone = await _dialogs.PromptAsync(_text["customers.add"], _text["customers.phone.hint"], _text["common.ok"], _text["common.cancel"], keyboard: Keyboard.Telephone);
        var customer = new Customer { Name = name, Phone = phone };
        var r = await _sender.Send(new SaveCustomerCommand(customer));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        await Shell.Current.GoToAsync(Routes.CustomerDetail, new Dictionary<string, object> { ["customerId"] = customer.Id.ToString("D") });
    }
}

/// <summary>
/// One customer's account: details and notes, udhaar/appu with promised date and reminders,
/// their own report, every previous bill (tap to view or resend the receipt), and the dues ledger.
/// </summary>
public sealed class CustomerDetailPage : ContentPage, IQueryAttributable
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly IPinService _pin;
    private readonly Entry _name;
    private readonly Entry _phone;
    private readonly Entry _address;
    private readonly Editor _notes;
    private readonly Label _balance = Ui.Amount();
    private readonly Label _oldest = Ui.Caption();
    private readonly DatePicker _promise = new() { Format = "dd MMM yyyy" };
    private readonly Label _promiseLabel = Ui.Body();
    private readonly VerticalStackLayout _report = new() { Spacing = 4 };
    private readonly VerticalStackLayout _receipts = new() { Spacing = 4 };
    private readonly VerticalStackLayout _ledger = new() { Spacing = 4 };
    private Customer? _customer;
    private bool _loading;

    public CustomerDetailPage(ISender sender, IDialogService dialogs, ILocalizer text, IPinService pin)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        _pin = pin;
        Title = text["customers.account"];

        _name = Ui.Entry(text["customers.name"]);
        _phone = Ui.Entry(text["customers.phone.hint"], Keyboard.Telephone);
        _address = Ui.Entry(text["customers.address"]);
        _notes = new Editor { Placeholder = text["customers.notes"], AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 60, BackgroundColor = Theme.Surface, FontFamily = Theme.Font, FontSize = 16 };
        var save = Ui.Secondary(text["customers.edit.save"]);
        save.Clicked += SafeUi.On(async () => await SaveDetailsAsync());

        _promise.DateSelected += SafeUi.On<DateChangedEventArgs>(async e =>
        {
            if (_loading || _customer is null) return;
            var r = await _sender.Send(new SetPromisedPayDateCommand(_customer.Id, DateOnly.FromDateTime((DateTime?)e.NewDate ?? DateTime.Today)));
            if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
            await LoadAsync();
        });
        var clearPromise = Ui.Secondary(text["customers.promise.clear"]);
        clearPromise.Clicked += SafeUi.On(async () =>
        {
            if (_customer is null) return;
            var cleared = await _sender.Send(new SetPromisedPayDateCommand(_customer.Id, null));
            if (cleared.IsFailure) await _dialogs.ToastAsync(_text.Error(cleared.Error));   // A1-09
            await LoadAsync();
        });
        var promiseRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) }, ColumnSpacing = 6 };
        promiseRow.Add(new VerticalStackLayout { Children = { Ui.Caption(text["customers.promise"]), _promiseLabel } }, 0, 0);
        promiseRow.Add(_promise, 1, 0);
        promiseRow.Add(clearPromise, 2, 0);

        var pay = Ui.Button(text["customers.pay"], Theme.Success);
        pay.Clicked += SafeUi.On(async () => await PayAsync());
        // SEC-5: bad debt is forgiven as a write-off (reason required), never disguised as a payment.
        var writeOff = Ui.Secondary(text["customers.writeoff"]);
        writeOff.Clicked += SafeUi.On(async () => await WriteOffAsync());
        var wa = Ui.Secondary(text["customers.remind.whatsapp"]);
        wa.Clicked += SafeUi.On(async () => await RemindAsync(ReminderChannel.WhatsApp));
        var sms = Ui.Secondary(text["customers.remind.sms"]);
        sms.Clicked += SafeUi.On(async () => await RemindAsync(ReminderChannel.Sms));
        var remindRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        remindRow.Add(wa, 0, 0);
        remindRow.Add(sms, 1, 0);

        Content = Ui.Scroll(
            Ui.Card(new VerticalStackLayout { Spacing = 6, Children = { _name, _phone, _address, _notes, save } }),
            Ui.Card(new VerticalStackLayout
            {
                Spacing = 8,
                Children = { Ui.Caption(text["customers.balance"]), _balance, _oldest, promiseRow, pay, writeOff, remindRow },
            }),
            Ui.Card(new VerticalStackLayout { Spacing = 4, Children = { Header(text["customers.report"]), _report } }),
            Ui.Card(new VerticalStackLayout { Spacing = 4, Children = { Header(text["customers.receipts"]), _receipts } }),
            Ui.Card(new VerticalStackLayout { Spacing = 4, Children = { Header(text["customers.ledger"]), _ledger } }));
    }

    private static Label Header(string text) => new() { Text = text, FontFamily = Theme.FontBold, FontSize = 18 };

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("customerId", out var id) && Guid.TryParse(id?.ToString(), out var cid))
        {
            _customer = new Customer { Id = cid };
            _ = SafeUi.Run(LoadAsync);   // A1-03
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // FIX(UI-1, 2026-10-20, FIX-3): Flaw: unwrapped async void OnAppearing on a ContentPage — a failed load
        // escaped SafeUi (no toast, half-loaded page). Fix: run the load through SafeUi.Run like the other 7
        // secondary pages. Why: "never-fail UI" must hold on every page. Logged in docs/AGENT_FIX_LOG.md.
        await SafeUi.Run(async () =>
        {
            if (_customer is not null) await LoadAsync();   // back from a bill or a payment
        });
    }

    private async Task LoadAsync()
    {
        if (_customer is null) return;
        var c = await _sender.Send(new GetCustomerQuery(_customer.Id));
        if (c is null) return;
        _customer = c;
        _loading = true;
        Title = c.Name;
        _name.Text = c.Name;
        _phone.Text = CustomerFormat.Phone(c.Phone);
        _address.Text = c.Address;
        _notes.Text = c.Notes;
        _balance.Text = IndianFormat.Rupees(c.DuesBalance);
        _balance.TextColor = c.DuesBalance.Value > 0 ? Theme.UdhaarColor : Theme.Success;
        _promiseLabel.Text = c.PromisedPayDate is { } p ? _text.Short(p) : _text["customers.promise.none"];
        _promise.Date = (c.PromisedPayDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(7))).ToDateTime(TimeOnly.MinValue);
        _loading = false;

        var report = await _sender.Send(new GetCustomerReportQuery(c.Id));
        _oldest.Text = report?.OldestUnpaidDays is { } days ? _text.Format("customers.report.oldest", days) : "";
        _report.Children.Clear();
        if (report is not null)
        {
            Row(_report, _text["customers.report.spent"], IndianFormat.Rupees(report.TotalSpent));
            Row(_report, _text["customers.report.visits"], report.BillCount.ToString(CultureInfo.InvariantCulture));
            Row(_report, _text["customers.report.avg"], IndianFormat.Rupees(report.AverageBill));
            Row(_report, _text["customers.report.first"], CustomerFormat.Day(report.FirstVisit));
            Row(_report, _text["customers.report.last"], CustomerFormat.Day(report.LastVisit));
            Row(_report, _text["tender.cash"], IndianFormat.Rupees(report.PaidCash));
            Row(_report, _text["tender.upi"], IndianFormat.Rupees(report.PaidUpi));
            Row(_report, _text["tender.udhaar"], IndianFormat.Rupees(report.OnUdhaar));
            Row(_report, _text["customers.report.paidsofar"], IndianFormat.Rupees(report.DuesPaidSoFar));
            // SEC-5: written-off dues are their own line, never folded into "paid so far" (collections).
            if (report.DuesWrittenOffSoFar.Value > 0)
                Row(_report, _text["customers.report.writtenoff"], IndianFormat.Rupees(report.DuesWrittenOffSoFar));
            if (report.TopItems.Count > 0)
            {
                _report.Add(Ui.Caption(_text["customers.report.usual"]));
                foreach (var t in report.TopItems)
                    Row(_report, $"{t.Name} · {IndianFormat.Quantity(t.Qty)} {_text.UnitLabel(t.Unit)}", _text.Format("customers.report.times", t.Times));
            }
        }

        _receipts.Children.Clear();
        var bills = await _sender.Send(new GetCustomerBillsQuery(c.Id, 50));
        if (bills.Count == 0) _receipts.Add(Ui.Caption(_text["common.empty"]));
        foreach (var b in bills)
        {
            var label = $"{b.Number} · {b.FinalizedAt.ToLocalTime():dd MMM, h:mm tt} · {CustomerFormat.TenderLabel(_text, b.Tender)}"
                        + (b.Status == BillStatus.Void ? " · " + _text["bills.void.badge"] : "");
            var row = Row(_receipts, label, IndianFormat.Rupees(b.GrandTotal));
            var billId = b.BillId.ToString("D");
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(SafeUi.Fire(() => Shell.Current.GoToAsync(Routes.BillDetail, new Dictionary<string, object> { ["billId"] = billId }))),   // A1-03
            });
        }

        _ledger.Children.Clear();
        var ledger = await _sender.Send(new GetCustomerLedgerQuery(c.Id));
        if (ledger.Count == 0) _ledger.Add(Ui.Caption(_text["customers.ledger.empty"]));
        foreach (var e in ledger)
        {
            var (label, sign) = e.Kind switch
            {
                DuesEntryKind.Charge => (_text["customers.charge"], "+"),
                DuesEntryKind.Payment => (_text["customers.payment"], "−"),
                // SEC-5: a write-off shows as itself in the ledger — not as a payment, not as a reversal.
                DuesEntryKind.WriteOff => (_text["customers.writeoff"], "−"),
                _ => (_text["customers.reversal"], "−"),
            };
            Row(_ledger, $"{label} · {e.CreatedAt.ToLocalTime():dd MMM, h:mm tt}", sign + IndianFormat.Rupees(e.Amount));
        }
    }

    private static Grid Row(Layout into, string label, string value)
    {
        var g = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8, MinimumHeightRequest = 32 };
        g.Add(Ui.Body(label, 15), 0, 0);
        g.Add(new Label { Text = value, FontFamily = Theme.FontBold, FontSize = 15, HorizontalTextAlignment = TextAlignment.End }, 1, 0);
        into.Add(g);
        return g;
    }

    private async Task SaveDetailsAsync()
    {
        if (_customer is null) return;
        _customer.Name = _name.Text ?? "";
        _customer.Phone = _phone.Text;
        _customer.Address = _address.Text;
        _customer.Notes = _notes.Text;
        var r = await _sender.Send(new SaveCustomerCommand(_customer));
        await _dialogs.ToastAsync(r.IsFailure ? _text.Error(r.Error) : _text["customers.saved"]);
        await LoadAsync();
    }

    private async Task PayAsync()
    {
        if (_customer is null) return;
        // FIX(SEC-4, 2026-10-20, FIX-3): recording a dues payment is a sensitive action — gated by the
        // owner PIN when one is set. Why: a fake "payment" is how udhaar quietly disappears from the
        // books. Logged in docs/AGENT_FIX_LOG.md.
        if (!await PinGate.CheckAsync(_pin, _dialogs, _text)) return;
        var value = await _dialogs.PromptAsync(_text["customers.pay"], _text["customers.pay.amount"], _text["common.ok"], _text["common.cancel"],
            IndianFormat.Rupees(_customer.DuesBalance).Replace("₹", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal), Keyboard.Numeric);
        if (value is null || !PosInput.TryParseRupees(value, out var amount) || amount.Value <= 0) return;
        var r = await _sender.Send(new RecordDuesPaymentCommand(_customer.Id, amount, null));
        await _dialogs.ToastAsync(r.IsFailure ? _text.Error(r.Error) : _text.Format("customers.pay.done", IndianFormat.Rupees(amount)));
        await LoadAsync();
    }

    private async Task RemindAsync(ReminderChannel channel)
    {
        if (_customer is null) return;
        var r = await _sender.Send(new SendDuesReminderCommand(_customer.Id, channel));
        if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
        await LoadAsync();
    }

    /// <summary>
    /// FIX(SEC-5, 2026-10-20, FIX-3): the UI half of the dues write-off (command layer: FIX-4, commit
    /// 0711c94b). Flaw: forgiving bad debt was only possible as a FAKE payment, which corrupted the
    /// collections reports and hid genuine payment fraud in the same bucket. Fix: a Write-off action on
    /// the customer's account — PIN-gated (SEC-4: write-off is on the sensitive list), amount prompt,
    /// then a MANDATORY reason prompt — sending RecordDuesWriteOffCommand. Ledger rows and the customer
    /// report show write-offs as their own line (DuesWrittenOffSoFar), never as payments. Why: forgiven
    /// debt and collected cash are different facts; the books must say which is which. Logged in
    /// docs/AGENT_FIX_LOG.md.
    /// </summary>
    private async Task WriteOffAsync()
    {
        if (_customer is null) return;
        if (!await PinGate.CheckAsync(_pin, _dialogs, _text)) return;
        var value = await _dialogs.PromptAsync(_text["customers.writeoff"], _text["customers.writeoff.amount"], _text["common.ok"], _text["common.cancel"],
            IndianFormat.Rupees(_customer.DuesBalance).Replace("₹", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal), Keyboard.Numeric);
        if (value is null || !PosInput.TryParseRupees(value, out var amount) || amount.Value <= 0) return;
        var reason = await _dialogs.PromptAsync(_text["customers.writeoff"], _text["customers.writeoff.reason"], _text["common.ok"], _text["common.cancel"]);
        if (string.IsNullOrWhiteSpace(reason)) return;   // a reason is mandatory — an empty one cancels the write-off
        var r = await _sender.Send(new RecordDuesWriteOffCommand(_customer.Id, amount, reason.Trim()));
        await _dialogs.ToastAsync(r.IsFailure ? _text.Error(r.Error) : _text.Format("customers.writeoff.done", IndianFormat.Rupees(amount)));
        await LoadAsync();
    }
}
