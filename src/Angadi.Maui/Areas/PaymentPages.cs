using Angadi.Application.Billing;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Maui.Areas.Pos;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

// v2 split tender + returns (docs/BILLING_RULES.md §Split tender, §Returns). Code-built pages like the others; every
// handler through SafeUi; all text from strings.payments.*.json.

/// <summary>What the split screen hands back to the counter: the amount of each tender (0 = not used).</summary>
public sealed record SplitPlan(Paise Cash, Paise Upi, Paise Udhaar, Paise CashReceived);

/// <summary>
/// Split tender: the shopkeeper types the cash and/or UPI parts on big number boxes; whatever is left goes to the
/// tender chosen under "Rest goes to" (UPI by default). The remaining amount updates as they type. Returns a
/// <see cref="SplitPlan"/> (null = cancelled); the counter then takes the UPI part's QR and the udhaar customer.
/// </summary>
public sealed class SplitPayPage : ContentPage, IQueryAttributable
{
    private readonly ShellNavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly Label _total = Ui.Amount();
    private readonly Entry _cash;
    private readonly Entry _upi;
    private readonly Entry _received;
    private readonly Label _remaining = new() { FontFamily = Theme.FontBold, FontSize = 22, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _change = Ui.Caption();
    private readonly Dictionary<TenderType, Button> _restButtons = [];
    private TenderType _restTo = TenderType.Upi;
    private Paise _bill = Paise.Zero;
    private bool _answered;

    public SplitPayPage(ShellNavigationService nav, IDialogService dialogs, ILocalizer text)
    {
        _nav = nav;
        _dialogs = dialogs;
        _text = text;
        Title = text["split.title"];
        _total.HorizontalTextAlignment = TextAlignment.Center;
        _cash = BigEntry(text["split.cash.part"]);
        _upi = BigEntry(text["split.upi.part"]);
        _received = Ui.Entry(text["split.cash.received"], Keyboard.Numeric);
        _received.FontSize = 20;
        _received.MinimumHeightRequest = Theme.BigTouch;
        foreach (var e in new[] { _cash, _upi, _received }) e.TextChanged += (_, _) => Refresh();

        var rest = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        var column = 0;
        foreach (var tender in new[] { TenderType.Cash, TenderType.Upi, TenderType.Udhaar })
        {
            var b = new Button { Text = TenderName(text, tender), MinimumHeightRequest = 56, FontSize = 17 };
            b.Clicked += (_, _) => { _restTo = tender; Refresh(); };
            _restButtons[tender] = b;
            rest.Add(b, column++, 0);
        }

        var go = Ui.Button(text["split.continue"], Theme.Success, big: true);
        go.Clicked += SafeUi.On(ContinueAsync);
        var cancel = Ui.Secondary(text["common.cancel"]);
        cancel.Clicked += SafeUi.On(async () => { _answered = true; await _nav.ReturnAsync<SplitPlan>(null); });

        Content = Ui.Scroll(
            Ui.Caption(text["split.hint"]), _total,
            Ui.Body(text["tender.cash"]), _cash,
            Ui.Body(text["tender.upi"]), _upi,
            Ui.Body(text["split.rest.to"]), rest,
            _remaining,
            Ui.Body(text["split.cash.received.title"]), _received, _change,
            go, cancel);
        Refresh();
    }

    private static Entry BigEntry(string placeholder)
    {
        var e = Ui.Entry(placeholder, Keyboard.Numeric);
        e.FontSize = 28;
        e.FontFamily = Theme.FontBold;
        e.MinimumHeightRequest = Theme.BigTouch;
        return e;
    }

    internal static string TenderName(ILocalizer text, TenderType tender) => tender switch
    {
        TenderType.Cash => text["tender.cash"],
        TenderType.Upi => text["tender.upi"],
        TenderType.Udhaar => text["tender.udhaar"],
        _ => text["tender.split"],
    };

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("total", out var t) && long.TryParse(t?.ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var paise))
            _bill = new Paise(paise);
        _total.Text = IndianFormat.Rupees(_bill);
        Refresh();
    }

    private static Paise Typed(Entry e) => PosInput.TryParseRupees(e.Text, out var p) && p.Value > 0 ? p : Paise.Zero;

    /// <summary>The parts as typed, with the rest added to the chosen tender. Null when the typed parts exceed the bill.</summary>
    private SplitPlan? Plan()
    {
        var cash = Typed(_cash).Value;
        var upi = Typed(_upi).Value;
        var rest = _bill.Value - cash - upi;
        if (rest < 0) return null;
        var udhaar = 0L;
        switch (_restTo)
        {
            case TenderType.Cash: cash += rest; break;
            case TenderType.Udhaar: udhaar = rest; break;
            default: upi += rest; break;
        }
        return new SplitPlan(new Paise(cash), new Paise(upi), new Paise(udhaar), Typed(_received));
    }

    private void Refresh()
    {
        foreach (var (tender, b) in _restButtons)
        {
            var on = tender == _restTo;
            b.BackgroundColor = on ? Theme.Primary : Theme.PrimarySoft;
            b.TextColor = on ? Colors.White : Theme.Primary;
        }
        var rest = _bill.Value - Typed(_cash).Value - Typed(_upi).Value;
        if (rest < 0)
        {
            _remaining.Text = _text.Format("split.over", IndianFormat.Rupees(new Paise(-rest)));
            _remaining.TextColor = Theme.Danger;
        }
        else
        {
            _remaining.Text = _text.Format("split.remaining", IndianFormat.Rupees(new Paise(rest)), TenderName(_text, _restTo));
            _remaining.TextColor = Theme.Primary;
        }
        var plan = Plan();
        _change.Text = plan is not null && plan.Cash.Value > 0 && plan.CashReceived > plan.Cash
            ? _text.Format("split.change", IndianFormat.Rupees(new Paise(plan.CashReceived.Value - plan.Cash.Value)))
            : "";
    }

    private async Task ContinueAsync()
    {
        var plan = Plan();
        var used = plan is null ? 0 : new[] { plan.Cash, plan.Upi, plan.Udhaar }.Count(p => p.Value > 0);
        if (plan is null || used < 2)
        {
            await _dialogs.ToastAsync(_text.Error(new Error(ErrorCodes.BillSplitInvalid)));
            return;
        }
        if (plan.CashReceived.Value > 0 && plan.CashReceived < plan.Cash)
        {
            await _dialogs.ToastAsync(_text.Error(Error.With(ErrorCodes.BillCashShort, IndianFormat.Rupees(new Paise(plan.Cash.Value - plan.CashReceived.Value)))));
            return;
        }
        _answered = true;
        await _nav.ReturnAfterBackAsync(plan);   // the counter goes on to the UPI screen: pop first, then hand over
    }

    /// <summary>Back = cancelled (same rule as the UPI screen).</summary>
    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        if (!_answered) { _answered = true; _nav.CancelPending(); }
    }
}

/// <summary>
/// Returns / partial refunds from the bill detail page: pick lines and quantities (never more than is left to
/// return), how the money goes back (cash, UPI, or the customer's udhaar), owner PIN (the same gate as void), then a
/// result screen with the R- number and the return note to share.
/// </summary>
public sealed class ReturnPage : ContentPage, IQueryAttributable
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 16 };
    private readonly List<(ReturnableLine Line, Entry Qty)> _rows = [];
    private readonly Dictionary<RefundMethod, Button> _methodButtons = [];
    private readonly Label _refund = new() { FontFamily = Theme.FontBold, FontSize = 24, TextColor = Theme.Primary, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Entry _reason;
    private BillReturns? _state;
    private RefundMethod _method = RefundMethod.Cash;
    private Guid _billId;
    private Guid _returnId = Guid.NewGuid();   // minted once per return: a double tap or retry records it once
    private bool _recorded;

    public ReturnPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["return.items"];
        _reason = Ui.Entry(text["return.reason.hint"]);
        Content = new ScrollView { Content = _body };
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("billId", out var id) && Guid.TryParse(id?.ToString(), out var billId))
        {
            _billId = billId;
            _returnId = Guid.NewGuid();
            _recorded = false;
            _ = SafeUi.Run(LoadAsync);
        }
    }

    private async Task LoadAsync()
    {
        _state = await _sender.Send(new GetBillReturnsQuery(_billId));
        _body.Children.Clear();
        _rows.Clear();
        _methodButtons.Clear();
        if (_state is null) return;
        _body.Add(Ui.Title(_text.Format("return.title", _state.Bill.Number)));
        if (!_state.CanReturn)
        {
            _body.Add(Ui.Caption(_text["return.nothing.left"]));
            return;
        }
        _body.Add(Ui.Caption(_text["return.hint"]));
        foreach (var l in _state.Lines.Where(l => l.Remaining.Milli > 0))
        {
            var unit = _text.UnitLabel(l.Line.Unit);
            var qty = Ui.Entry("0", Keyboard.Numeric);
            qty.FontSize = 22;
            qty.MinimumHeightRequest = 56;
            qty.TextChanged += (_, _) => Refresh();
            var all = Ui.Secondary(_text["return.all"]);
            all.MinimumHeightRequest = 56;
            var left = l.Remaining;
            all.Clicked += (_, _) => qty.Text = IndianFormat.Quantity(left);
            var entryRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
            entryRow.Add(qty, 0, 0);
            entryRow.Add(all, 1, 0);
            _body.Add(Ui.Card(new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = _text.Name(l.Line), FontFamily = Theme.FontBold, FontSize = 17 },
                    Ui.Caption(_text.Format("return.line.state", IndianFormat.Quantity(l.Line.Qty), IndianFormat.Quantity(l.Returned),
                        IndianFormat.Quantity(l.Remaining), unit, IndianFormat.Rupees(l.Line.Net))),
                    entryRow,
                },
            }));
            _rows.Add((l, qty));
        }

        _body.Add(Ui.Body(_text["return.method"]));
        var methods = new Grid { ColumnSpacing = 8 };
        var col = 0;
        var options = new List<RefundMethod> { RefundMethod.Cash, RefundMethod.Upi };
        if (_state.Bill.CustomerId is not null) options.Add(RefundMethod.Dues);   // udhaar can only be cut for the bill's customer
        foreach (var m in options)
        {
            methods.ColumnDefinitions.Add(new(GridLength.Star));
            var b = new Button { Text = _text["return.method." + m.ToString().ToLowerInvariant()], MinimumHeightRequest = 56, FontSize = 16 };
            b.Clicked += (_, _) => { _method = m; Refresh(); };
            _methodButtons[m] = b;
            methods.Add(b, col++, 0);
        }
        if (!options.Contains(_method)) _method = RefundMethod.Cash;
        _body.Add(methods);
        _body.Add(_reason);
        _body.Add(_refund);
        var record = Ui.Button(_text["return.record"], Theme.Danger, big: true);
        record.Clicked += SafeUi.On(RecordAsync);
        _body.Add(record);
        Refresh();
    }

    /// <summary>The lines asked for; null when a quantity is not a number or more than is left.</summary>
    private List<ReturnRequestLine>? Requested(out string? problem)
    {
        problem = null;
        var list = new List<ReturnRequestLine>();
        foreach (var (line, entry) in _rows)
        {
            if (string.IsNullOrWhiteSpace(entry.Text)) continue;
            if (!PosInput.TryParseQty(entry.Text, out var qty) || qty.Milli < 0) { problem = _text.Error(new Error(ErrorCodes.BillQtyInvalid)); return null; }
            if (qty.Milli == 0) continue;
            if (qty > line.Remaining)
            {
                problem = _text.Error(Error.With(ErrorCodes.ReturnQtyTooLarge, _text.Name(line.Line), IndianFormat.Quantity(line.Remaining)));
                return null;
            }
            list.Add(new ReturnRequestLine(line.Line.Id, qty));
        }
        return list;
    }

    private void Refresh()
    {
        foreach (var (m, b) in _methodButtons)
        {
            var on = m == _method;
            b.BackgroundColor = on ? Theme.Primary : Theme.PrimarySoft;
            b.TextColor = on ? Colors.White : Theme.Primary;
        }
        if (_state is null) return;
        var asked = Requested(out var problem);
        if (asked is null) { _refund.Text = problem ?? ""; _refund.TextColor = Theme.Danger; return; }
        _refund.TextColor = Theme.Primary;
        // Preview with the same exact maths the handler uses (cumulative per bill).
        var preview = new SalesReturn { IsTaxInvoice = _state.Bill.IsTaxInvoice };
        foreach (var a in asked)
        {
            var row = _rows.First(r => r.Line.Line.Id == a.BillLineId).Line;
            preview.Lines.Add(new ReturnLine { BillLineId = a.BillLineId, GstSlab = row.Line.GstSlab, Qty = a.Qty, Amount = ReturnCalculator.LineAmount(row.Line, row.Returned, a.Qty) });
        }
        ReturnCalculator.Compute(preview, _state.Returns);
        _refund.Text = _text.Format("return.refund.preview", IndianFormat.Rupees(preview.RefundTotal));
    }

    private async Task RecordAsync()
    {
        if (_recorded || _state is null) return;
        var asked = Requested(out var problem);
        if (asked is null) { await _dialogs.ToastAsync(problem ?? ""); return; }
        if (asked.Count == 0) { await _dialogs.ToastAsync(_text.Error(new Error(ErrorCodes.ReturnEmpty))); return; }
        // Owner-only, through the same PIN gate as void (SEC-4): a refund hands money out of the drawer.
        if (!await PinGate.CheckAsync(Angadi.Application.Security.Permission.Refund, _dialogs, _text)) return;
        string? upiRef = null;
        if (_method == RefundMethod.Upi)
            upiRef = await _dialogs.PromptAsync(_text["tender.upi"], _text["pos.upi.ref"], _text["common.ok"], _text["common.cancel"], keyboard: Keyboard.Numeric);
        var r = await _sender.Send(new RecordReturnCommand(_returnId, _billId, asked, _method, upiRef, _reason.Text));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        _recorded = true;
        ShowDone(r.Value);
    }

    private void ShowDone(RecordedReturn done)
    {
        _body.Children.Clear();
        _body.Add(Ui.Title(_text["return.done"]));
        _body.Add(new Label { Text = done.Number, FontFamily = Theme.FontBold, FontSize = 30, HorizontalTextAlignment = TextAlignment.Center });
        _body.Add(new Label
        {
            Text = _text.Format("return.done.refund", IndianFormat.Rupees(done.RefundTotal)),
            FontFamily = Theme.FontBold, FontSize = 24, TextColor = Theme.Primary, HorizontalTextAlignment = TextAlignment.Center,
        });
        if (done.CashRefund.Value > 0) _body.Add(Ui.Body(_text.Format("return.done.cash", IndianFormat.Rupees(done.CashRefund)), 18));
        if (done.UpiRefund.Value > 0) _body.Add(Ui.Body(_text.Format("return.done.upi", IndianFormat.Rupees(done.UpiRefund)), 18));
        if (done.DuesCredit.Value > 0) _body.Add(Ui.Body(_text.Format("return.done.dues", IndianFormat.Rupees(done.DuesCredit)), 18));
        var share = Ui.Button(_text["return.share"], Theme.Success, big: true);
        var id = done.ReturnId;
        share.Clicked += SafeUi.On(async () =>
        {
            var s = await _sender.Send(new ShareReturnNoteCommand(id));
            if (s.IsFailure) await _dialogs.ToastAsync(_text.Error(s.Error));
        });
        // v2 printing: the return note on the Bluetooth thermal printer.
        var print = Ui.Button(_text["return.print"], Theme.Primary, big: true);
        print.Clicked += SafeUi.On(() => PrintActions.PrintAsync(_sender, _dialogs, _text, new Angadi.Application.Printing.PrintReturnNoteCommand(id)));
        var close = Ui.Secondary(_text["return.close"]);
        close.MinimumHeightRequest = Theme.BigTouch;
        close.Clicked += SafeUi.On(() => Shell.Current.GoToAsync(".."));
        _body.Add(share);
        _body.Add(print);
        _body.Add(close);
    }
}
