using System.Globalization;
using Angadi.Application.Common;
using Angadi.Application.Payroll;
using Angadi.Application.Platform;
using Angadi.Domain.Common;
using Angadi.Domain.Payroll;
using Angadi.Maui.Areas.Pos;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

/// <summary>
/// Settings → Staff pay: the people who work for the shop, today's attendance, advances and a monthly pay slip that can be
/// sent on WhatsApp. Marking attendance is for managers and the owner; paying salary and advances are owner only (the
/// request pipeline asks for the owner's PIN, so this screen cannot skip it).
/// </summary>
public sealed class PayrollPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly IClock _clock;
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };

    public PayrollPage(ISender sender, IDialogService dialogs, ILocalizer text, IClock clock)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        _clock = clock;
        Title = text["settings.payroll"];
        var add = Ui.Button(text["payroll.add"], big: true);
        add.Clicked += SafeUi.On(AddAsync);
        Content = Ui.Scroll(Ui.Title(text["payroll.title"]), Ui.Caption(text["payroll.hint"]), _list, add);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await SafeUi.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var today = _clock.Today;
        var workersResult = await _sender.Send(new ListWorkersQuery());
        var marksResult = await _sender.Send(new GetAttendanceQuery(today.Year, today.Month));
        _list.Clear();
        if (workersResult.IsFailure || marksResult.IsFailure)
        {
            await _dialogs.ToastAsync(_text.Error(workersResult.IsFailure ? workersResult.Error : marksResult.Error));
            return;
        }
        var workers = workersResult.Value;
        var marks = marksResult.Value;
        if (workers.Count == 0) _list.Add(Ui.Caption(_text["payroll.none"]));
        foreach (var w in workers) _list.Add(Card(w, marks.FirstOrDefault(m => m.WorkerId == w.Id && m.Day == today)));
    }

    private View Card(Worker w, AttendanceEntry? todayMark)
    {
        var stack = new VerticalStackLayout { Spacing = 4 };
        stack.Add(Ui.Title(w.Name));
        stack.Add(Ui.Caption(_text.Format(w.PayType == PayType.Monthly ? "payroll.line.monthly" : "payroll.line.daily", IndianFormat.Rupees(w.Rate))));
        var status = todayMark is null ? "" : " " + MarkName(todayMark.Mark);
        stack.Add(Ui.Caption(_text["payroll.today"] + status));

        var row = new HorizontalStackLayout { Spacing = 6 };
        foreach (var mark in new[] { AttendanceMark.Present, AttendanceMark.HalfDay, AttendanceMark.Absent })
        {
            var m = mark;
            var b = Ui.Secondary(MarkName(m));
            b.Clicked += SafeUi.On(async () => await MarkAsync(w, m));
            row.Add(b);
        }
        stack.Add(row);

        var actions = new HorizontalStackLayout { Spacing = 6 };
        var advance = Ui.Secondary(_text["payroll.advance"]);
        advance.Clicked += SafeUi.On(async () => await AdvanceAsync(w));
        var slip = Ui.Secondary(_text["payroll.payslip"]);
        slip.Clicked += SafeUi.On(async () => await PayslipAsync(w));
        var edit = Ui.Secondary(_text["payroll.edit"]);
        edit.Clicked += SafeUi.On(async () => await EditAsync(w));
        actions.Add(advance);
        actions.Add(slip);
        actions.Add(edit);
        stack.Add(actions);
        return Ui.Card(stack, padding: 10);
    }

    private string MarkName(AttendanceMark m) => m switch
    {
        AttendanceMark.Present => _text["payroll.present"],
        AttendanceMark.HalfDay => _text["payroll.half"],
        _ => _text["payroll.absent"],
    };

    private async Task MarkAsync(Worker w, AttendanceMark mark)
    {
        var r = await _sender.Send(new MarkAttendanceCommand(w.Id, _clock.Today, mark));
        await _dialogs.ToastAsync(r.IsSuccess ? _text["payroll.marked"] : _text.Error(r.Error));
        if (r.IsSuccess) await RefreshAsync();
    }

    private async Task AddAsync()
    {
        var name = await _dialogs.PromptAsync(_text["payroll.add"], _text["payroll.name.prompt"], _text["common.ok"], _text["common.cancel"]);
        if (string.IsNullOrWhiteSpace(name)) return;
        var phone = await _dialogs.PromptAsync(_text["payroll.add"], _text["payroll.phone.prompt"], _text["common.ok"], _text["common.cancel"], keyboard: Keyboard.Telephone);
        if (phone is null) return;
        var monthly = _text["payroll.type.monthly"];
        var daily = _text["payroll.type.daily"];
        var type = await _dialogs.ChooseAsync(_text["payroll.type.pick"], _text["common.cancel"], monthly, daily);
        if (type is null) return;
        var payType = type == monthly ? PayType.Monthly : PayType.Daily;
        if (await AskRateAsync(payType) is not { } rate) return;
        var r = await _sender.Send(new SaveWorkerCommand(new Worker { Name = name, Phone = phone, PayType = payType, Rate = rate }));
        await _dialogs.ToastAsync(r.IsSuccess ? _text["settings.saved"] : _text.Error(r.Error));
        await RefreshAsync();
    }

    private async Task<Paise?> AskRateAsync(PayType type)
    {
        var value = await _dialogs.PromptAsync(_text["payroll.title"], _text[type == PayType.Monthly ? "payroll.rate.monthly" : "payroll.rate.daily"],
            _text["common.ok"], _text["common.cancel"], keyboard: Keyboard.Numeric);
        if (value is null) return null;
        if (!PosInput.TryParseRupees(value, out var rate) || rate.Value <= 0)
        {
            await _dialogs.ToastAsync(_text.Error(new Error(ErrorCodes.WorkerRateInvalid)));
            return null;
        }
        return rate;
    }

    private async Task EditAsync(Worker w)
    {
        var rate = _text["payroll.edit.rate"];
        var off = _text["payroll.edit.off"];
        var choice = await _dialogs.ChooseAsync(w.Name, _text["common.cancel"], rate, off);
        if (choice is null) return;
        if (choice == rate)
        {
            if (await AskRateAsync(w.PayType) is not { } newRate) return;
            w.Rate = newRate;
        }
        else
        {
            w.IsActive = false;
        }
        var r = await _sender.Send(new SaveWorkerCommand(w));
        await _dialogs.ToastAsync(r.IsSuccess ? _text["settings.saved"] : _text.Error(r.Error));
        await RefreshAsync();
    }

    private async Task AdvanceAsync(Worker w)
    {
        var value = await _dialogs.PromptAsync(_text["payroll.advance"], _text.Format("payroll.advance.prompt", w.Name),
            _text["common.ok"], _text["common.cancel"], keyboard: Keyboard.Numeric);
        if (value is null) return;
        if (!PosInput.TryParseRupees(value, out var amount) || amount.Value <= 0)
        {
            await _dialogs.ToastAsync(_text.Error(new Error(ErrorCodes.PayAmountInvalid)));
            return;
        }
        var note = await _dialogs.PromptAsync(_text["payroll.advance"], _text["payroll.advance.note"], _text["common.ok"], _text["common.cancel"]);
        var r = await _sender.Send(new GiveAdvanceCommand(w.Id, amount, note));
        await _dialogs.ToastAsync(r.IsSuccess ? _text["settings.saved"] : _text.Error(r.Error));
    }

    private async Task PayslipAsync(Worker w)
    {
        var today = _clock.Today;
        var thisMonth = _text["payroll.month.this"];
        var lastMonth = _text["payroll.month.last"];
        var choice = await _dialogs.ChooseAsync(_text["payroll.month.pick"], _text["common.cancel"], thisMonth, lastMonth);
        if (choice is null) return;
        var d = choice == thisMonth ? today : today.AddMonths(-1);
        var got = await _sender.Send(new GetPayslipQuery(w.Id, d.Year, d.Month));
        if (got.IsFailure) { await _dialogs.ToastAsync(_text.Error(got.Error)); return; }
        var s = got.Value;
        var summary = _text.Format("payroll.slip.view", s.WorkerName, MonthLabel(d), s.PresentDays + (s.HalfDayCount > 0 ? " + " + s.HalfDayCount + "½" : ""),
            s.AbsentDays, IndianFormat.Rupees(s.Earned), IndianFormat.Rupees(s.Deduction), IndianFormat.Rupees(s.NetPay), IndianFormat.Rupees(s.BalanceAfter));

        if (s.AlreadyPaid)
        {
            if (await _dialogs.ConfirmAsync(_text["payroll.paid"], summary, _text["payroll.share"], _text["payroll.close"])) await ShareAsync(w, d);
            return;
        }
        if (!await _dialogs.ConfirmAsync(_text["payroll.payslip"], summary, _text["payroll.pay"], _text["payroll.close"])) return;
        var paid = await _sender.Send(new PaySalaryCommand(w.Id, d.Year, d.Month));
        if (paid.IsFailure) { await _dialogs.ToastAsync(_text.Error(paid.Error)); return; }
        await _dialogs.ToastAsync(_text["payroll.paid"]);
        if (await _dialogs.ConfirmAsync(_text["payroll.paid"], summary, _text["payroll.share"], _text["payroll.close"])) await ShareAsync(w, d);
    }

    private async Task ShareAsync(Worker w, DateOnly d)
    {
        var r = await _sender.Send(new SharePayslipCommand(w.Id, d.Year, d.Month));
        if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
    }

    private static string MonthLabel(DateOnly d) => d.ToString("MMM yyyy", CultureInfo.InvariantCulture);
}
