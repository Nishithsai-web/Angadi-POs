using Angadi.Application.Common;
using Angadi.Application.Payroll;
using Angadi.Application.Platform;
using Angadi.Application.Security;
using Angadi.Domain.Common;
using Angadi.Domain.Payroll;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>Staff money: the pay arithmetic, the attendance / advance / salary flow, and who may do what.</summary>
public sealed class PayrollTests : IDisposable
{
    private readonly AppHost _h = new();   // fake clock: 19 Oct 2026
    public void Dispose() => _h.Dispose();

    private static DateOnly Oct(int day) => new(2026, 10, day);

    private async Task<Worker> AddWorkerAsync(PayType type, long ratePaise, string name = "Ravi")
    {
        var w = new Worker { Name = name, Phone = "98765 43210", PayType = type, Rate = new Paise(ratePaise) };
        var r = await _h.Sender.Send(new SaveWorkerCommand(w));
        Assert.True(r.IsSuccess, r.Error.Code);
        return w;
    }

    private async Task PresentAsync(Worker w, int from, int to)
    {
        for (var d = from; d <= to; d++)
            Assert.True((await _h.Sender.Send(new MarkAttendanceCommand(w.Id, Oct(d), AttendanceMark.Present))).IsSuccess);
    }

    // ---- arithmetic

    [Fact]
    public void Monthly_PaysByTheDayOfTheMonth_Daily_ByTheDayWorked()
    {
        Assert.Equal(3_100_000, PayrollCalculator.Earned(PayType.Monthly, new Paise(3_100_000), 62, 31).Value);   // every day of a 31-day month
        Assert.Equal(1_050_000, PayrollCalculator.Earned(PayType.Monthly, new Paise(3_100_000), 21, 31).Value);   // 10 days + a half day
        Assert.Equal(175_000, PayrollCalculator.Earned(PayType.Daily, new Paise(50_000), 7, 31).Value);           // 3 days + a half day
        Assert.Equal(0, PayrollCalculator.Earned(PayType.Daily, new Paise(50_000), 0, 31).Value);
    }

    [Fact]
    public void Rounding_IsHalfUp_InWholePaise() =>
        Assert.Equal(51, PayrollCalculator.Earned(PayType.Daily, new Paise(101), 1, 30).Value);

    [Fact]
    public void AnAdvance_IsTakenFromSalary_NeverMoreThanWasEarned()
    {
        var w = new Worker { Name = "Ravi", PayType = PayType.Daily, Rate = new Paise(50_000) };
        var marks = Enumerable.Range(1, 3).Select(d => new AttendanceEntry(w.Id, Oct(d), AttendanceMark.Present))
            .Append(new AttendanceEntry(w.Id, Oct(4), AttendanceMark.HalfDay)).ToList();
        var slip = PayrollCalculator.Compute(w, 2026, 10, marks, new Paise(200_000), alreadyPaid: false);
        Assert.Equal(175_000, slip.Earned.Value);
        Assert.Equal(175_000, slip.Deduction.Value);
        Assert.Equal(0, slip.NetPay.Value);
        Assert.Equal(25_000, slip.BalanceAfter.Value);   // the rest carries to next month
    }

    [Fact]
    public void AnotherWorkersOrAnotherMonthsAttendance_IsIgnored()
    {
        var w = new Worker { Name = "Ravi", PayType = PayType.Daily, Rate = new Paise(50_000) };
        var marks = new[]
        {
            new AttendanceEntry(w.Id, Oct(1), AttendanceMark.Present),
            new AttendanceEntry(Guid.NewGuid(), Oct(2), AttendanceMark.Present),
            new AttendanceEntry(w.Id, new DateOnly(2026, 9, 30), AttendanceMark.Present),
        };
        Assert.Equal(50_000, PayrollCalculator.Compute(w, 2026, 10, marks, Paise.Zero, false).Earned.Value);
    }

    // ---- the flow

    [Fact]
    public async Task Attendance_AdvanceAndSalary_FlowEndToEnd_AndTheMonthPaysOnlyOnce()
    {
        await _h.SeedShopAsync();
        var w = await AddWorkerAsync(PayType.Daily, 50_000);
        await PresentAsync(w, 15, 19);                                         // 5 days
        Assert.True((await _h.Sender.Send(new GiveAdvanceCommand(w.Id, new Paise(10_000), "festival"))).IsSuccess);

        var preview = (await _h.Sender.Send(new GetPayslipQuery(w.Id, 2026, 10))).Value;
        Assert.Equal(250_000, preview.Earned.Value);
        Assert.Equal(10_000, preview.Deduction.Value);
        Assert.False(preview.AlreadyPaid);

        var paid = await _h.Sender.Send(new PaySalaryCommand(w.Id, 2026, 10));
        Assert.True(paid.IsSuccess, paid.Error.Code);
        Assert.Equal(240_000, paid.Value.NetPay.Value);

        var after = (await _h.Sender.Send(new GetPayslipQuery(w.Id, 2026, 10))).Value;
        Assert.True(after.AlreadyPaid);
        Assert.Equal(0, after.AdvanceBalance.Value);   // the advance was recovered
        Assert.Equal(ErrorCodes.SalaryAlreadyPaid, (await _h.Sender.Send(new PaySalaryCommand(w.Id, 2026, 10))).Error.Code);
    }

    [Fact]
    public async Task MarkingADayAgain_ReplacesTheMark()
    {
        await _h.SeedShopAsync();
        var w = await AddWorkerAsync(PayType.Daily, 50_000);
        await PresentAsync(w, 15, 19);
        await _h.Sender.Send(new MarkAttendanceCommand(w.Id, Oct(19), AttendanceMark.Absent));
        Assert.Equal(200_000, (await _h.Sender.Send(new GetPayslipQuery(w.Id, 2026, 10))).Value.Earned.Value);
    }

    [Fact]
    public async Task Guards_FutureDays_FutureMonths_NoAttendance_UnknownWorker_BadInput()
    {
        await _h.SeedShopAsync();
        var w = await AddWorkerAsync(PayType.Monthly, 3_100_000);
        Assert.Equal(ErrorCodes.AttendanceFuture, (await _h.Sender.Send(new MarkAttendanceCommand(w.Id, Oct(20), AttendanceMark.Present))).Error.Code);
        Assert.Equal(ErrorCodes.PayMonthInvalid, (await _h.Sender.Send(new GetPayslipQuery(w.Id, 2026, 11))).Error.Code);
        Assert.Equal(ErrorCodes.PayMonthInvalid, (await _h.Sender.Send(new GetPayslipQuery(w.Id, 2026, 13))).Error.Code);
        Assert.Equal(ErrorCodes.NothingToPay, (await _h.Sender.Send(new PaySalaryCommand(w.Id, 2026, 10))).Error.Code);
        Assert.Equal(ErrorCodes.WorkerNotFound, (await _h.Sender.Send(new GiveAdvanceCommand(Guid.NewGuid(), new Paise(100), null))).Error.Code);
        Assert.Equal(ErrorCodes.PayAmountInvalid, (await _h.Sender.Send(new GiveAdvanceCommand(w.Id, Paise.Zero, null))).Error.Code);
        Assert.Equal(ErrorCodes.WorkerNameRequired, (await _h.Sender.Send(new SaveWorkerCommand(new Worker { Name = " ", Rate = new Paise(100) }))).Error.Code);
        Assert.Equal(ErrorCodes.WorkerRateInvalid, (await _h.Sender.Send(new SaveWorkerCommand(new Worker { Name = "A", Rate = Paise.Zero }))).Error.Code);
    }

    [Fact]
    public async Task RemovedWorkers_LeaveTheList_ButKeepTheirHistory()
    {
        await _h.SeedShopAsync();
        var w = await AddWorkerAsync(PayType.Daily, 50_000);
        w.IsActive = false;
        await _h.Sender.Send(new SaveWorkerCommand(w));
        Assert.Empty(await _h.Sender.Send(new ListWorkersQuery()));
        Assert.Single(await _h.Sender.Send(new ListWorkersQuery(IncludeInactive: true)));
    }

    [Fact]
    public async Task SharePayslip_GoesToTheWorkersWhatsApp()
    {
        await _h.SeedShopAsync();
        var w = await AddWorkerAsync(PayType.Daily, 50_000);
        await PresentAsync(w, 17, 19);
        Assert.True((await _h.Sender.Send(new SharePayslipCommand(w.Id, 2026, 10))).IsSuccess);
        var sent = Assert.Single(_h.Share.WhatsApp);
        Assert.Equal("919876543210", sent.Phone);
        Assert.Contains("Ravi", sent.Message);
    }

    // ---- who may do what

    [Fact]
    public async Task ManagerMarksAttendance_ButOnlyTheOwnerPaysOrGivesAdvances()
    {
        await _h.SeedShopAsync();
        var w = await AddWorkerAsync(PayType.Daily, 50_000);
        await _h.Services.GetRequiredService<IPinService>().SetPinAsync("4321");
        var access = _h.Services.GetRequiredService<IAccessControl>();
        await access.SignInAsync("4321");
        await _h.Sender.Send(new SaveStaffCommand(null, "Lakshmi", Role.Manager, "1111"));
        access.SignOut();
        await access.SignInAsync("1111");

        Assert.True((await _h.Sender.Send(new MarkAttendanceCommand(w.Id, Oct(19), AttendanceMark.Present))).IsSuccess);
        Assert.Equal(ErrorCodes.NotAllowedOwner, (await _h.Sender.Send(new GiveAdvanceCommand(w.Id, new Paise(1000), null))).Error.Code);
        Assert.Equal(ErrorCodes.NotAllowedOwner, (await _h.Sender.Send(new PaySalaryCommand(w.Id, 2026, 10))).Error.Code);
    }
}
