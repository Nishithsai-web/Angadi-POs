using Angadi.Application.Billing;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Common;
using Angadi.Domain.Events;
using Angadi.Domain.Payroll;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Application.Payroll;

// ---------- Validators ----------

internal sealed class SaveWorkerValidator : IValidator<SaveWorkerCommand>
{
    public Error? Validate(SaveWorkerCommand c)
    {
        var w = c.Worker;
        if (string.IsNullOrWhiteSpace(w.Name) || w.Name.Trim().Length > 60) return new Error(ErrorCodes.WorkerNameRequired);
        if (w.Rate.Value <= 0 || w.Rate.Value > PayRules.MaxRatePaise) return new Error(ErrorCodes.WorkerRateInvalid);
        if (!string.IsNullOrWhiteSpace(w.Phone) && PhoneNumbers.Normalize(w.Phone) is null) return new Error(ErrorCodes.PhoneInvalid);
        return null;
    }
}

internal sealed class GiveAdvanceValidator : IValidator<GiveAdvanceCommand>
{
    public Error? Validate(GiveAdvanceCommand c) =>
        c.Amount.Value <= 0 || c.Amount.Value > PayRules.MaxRatePaise ? new Error(ErrorCodes.PayAmountInvalid) : null;
}

internal sealed class PayMonthValidator :
    IValidator<GetPayslipQuery>, IValidator<PaySalaryCommand>, IValidator<SharePayslipCommand>
{
    public Error? Validate(GetPayslipQuery r) => Check(r.Year, r.Month);
    public Error? Validate(PaySalaryCommand r) => Check(r.Year, r.Month);
    public Error? Validate(SharePayslipCommand r) => Check(r.Year, r.Month);
    private static Error? Check(int year, int month) =>
        year is < 2000 or > 2100 || month is < 1 or > 12 ? new Error(ErrorCodes.PayMonthInvalid) : null;
}

public static class PayRules
{
    /// <summary>Upper bound for one rate or advance (₹1 crore): a typo guard, not a business rule.</summary>
    public const long MaxRatePaise = 10_000_000_00L;
}

// ---------- Workers and attendance ----------

internal sealed class WorkerHandlers(IPayrollRepository repo, IOutbox outbox, IClock clock) :
    IRequestHandler<SaveWorkerCommand, Result<Guid>>,
    IRequestHandler<ListWorkersQuery, Result<IReadOnlyList<Worker>>>,
    IRequestHandler<MarkAttendanceCommand, Result>,
    IRequestHandler<GetAttendanceQuery, Result<IReadOnlyList<AttendanceEntry>>>
{
    public async Task<Result<Guid>> Handle(SaveWorkerCommand command, CancellationToken ct)
    {
        var w = command.Worker;
        w.Name = w.Name.Trim();
        w.Phone = string.IsNullOrWhiteSpace(w.Phone) ? null : w.Phone.Trim();
        await repo.SaveWorkerAsync(w, ct);
        await outbox.AppendAsync("Worker", new WorkerChanged(w.Id, clock.Now), ct);
        return Result.Success(w.Id);
    }

    public async Task<Result<IReadOnlyList<Worker>>> Handle(ListWorkersQuery query, CancellationToken ct) =>
        Result<IReadOnlyList<Worker>>.Success(await repo.ListWorkersAsync(query.IncludeInactive, ct));

    public async Task<Result> Handle(MarkAttendanceCommand command, CancellationToken ct)
    {
        if (command.Day > clock.Today) return Result.Failure(ErrorCodes.AttendanceFuture);
        if (await repo.GetWorkerAsync(command.WorkerId, ct) is null) return Result.Failure(ErrorCodes.WorkerNotFound);
        await repo.SetAttendanceAsync(new AttendanceEntry(command.WorkerId, command.Day, command.Mark), ct);
        await outbox.AppendAsync("Attendance", new AttendanceMarked(command.WorkerId, clock.Now, command.Day.Year * 10000 + command.Day.Month * 100 + command.Day.Day, (int)command.Mark), ct);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<AttendanceEntry>>> Handle(GetAttendanceQuery query, CancellationToken ct) =>
        Result<IReadOnlyList<AttendanceEntry>>.Success(await repo.GetAttendanceAsync(query.Year, query.Month, ct));
}

// ---------- Advances, payslips, salary ----------

internal sealed class PayHandlers(IPayrollRepository repo, IOutbox outbox, IClock clock, IShopProfileRepository shop, IReceiptShare share, ILocalizer text) :
    IRequestHandler<GiveAdvanceCommand, Result>,
    IRequestHandler<GetPayslipQuery, Result<Payslip>>,
    IRequestHandler<PaySalaryCommand, Result<Payslip>>,
    IRequestHandler<SharePayslipCommand, Result>
{
    public async Task<Result> Handle(GiveAdvanceCommand command, CancellationToken ct)
    {
        if (await repo.GetWorkerAsync(command.WorkerId, ct) is null) return Result.Failure(ErrorCodes.WorkerNotFound);
        await Record(command.WorkerId, PayEntryKind.Advance, command.Amount, 0, command.Note, ct);
        return Result.Success();
    }

    public async Task<Result<Payslip>> Handle(GetPayslipQuery query, CancellationToken ct) => await Build(query.WorkerId, query.Year, query.Month, ct);

    public async Task<Result<Payslip>> Handle(PaySalaryCommand command, CancellationToken ct)
    {
        var built = await Build(command.WorkerId, command.Year, command.Month, ct);
        if (built.IsFailure) return built;
        var slip = built.Value;
        if (slip.AlreadyPaid) return Result.Failure<Payslip>(ErrorCodes.SalaryAlreadyPaid);
        if (slip.Earned.Value <= 0) return Result.Failure<Payslip>(ErrorCodes.NothingToPay);
        var month = command.Year * 100 + command.Month;
        if (slip.Deduction.Value > 0) await Record(slip.WorkerId, PayEntryKind.Recovery, slip.Deduction, month, null, ct);
        // The salary row is written even when the advance swallowed it all (net 0): it is what closes the month.
        await Record(slip.WorkerId, PayEntryKind.Salary, slip.NetPay, month, null, ct);
        return Result.Success(slip with { AlreadyPaid = true });
    }

    public async Task<Result> Handle(SharePayslipCommand command, CancellationToken ct)
    {
        var built = await Build(command.WorkerId, command.Year, command.Month, ct);
        if (built.IsFailure) return Result.Failure(built.Error);
        var s = built.Value;
        var worker = await repo.GetWorkerAsync(command.WorkerId, ct);
        var profile = await shop.GetAsync(ct);
        var message = text.Format("payroll.slip.message", s.WorkerName, s.Month.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "/" + s.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile?.Name ?? "", s.PresentDays + (s.HalfDayCount > 0 ? " + " + s.HalfDayCount + "½" : ""), IndianFormat.Rupees(s.Earned),
            IndianFormat.Rupees(s.Deduction), IndianFormat.Rupees(s.NetPay), IndianFormat.Rupees(s.BalanceAfter));
        if (PhoneNumbers.Normalize(worker?.Phone) is { } phone)
        {
            var sent = await share.OpenWhatsAppAsync(phone, message, ct);
            if (sent.IsSuccess) return sent;
        }
        return await share.ShareTextAsync(message, text["payroll.slip.title"], ct);
    }

    private async Task<Result<Payslip>> Build(Guid workerId, int year, int month, CancellationToken ct)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12 || new DateOnly(year, month, 1) > clock.Today) return Result.Failure<Payslip>(ErrorCodes.PayMonthInvalid);
        if (await repo.GetWorkerAsync(workerId, ct) is not { } worker) return Result.Failure<Payslip>(ErrorCodes.WorkerNotFound);
        var marks = await repo.GetAttendanceAsync(year, month, ct);
        var balance = await repo.GetAdvanceBalanceAsync(workerId, ct);
        var paid = await repo.IsSalaryPaidAsync(workerId, year * 100 + month, ct);
        return Result.Success(PayrollCalculator.Compute(worker, year, month, marks, balance, paid));
    }

    private async Task Record(Guid workerId, PayEntryKind kind, Paise amount, int payMonth, string? note, CancellationToken ct)
    {
        var entry = new PayEntry
        {
            WorkerId = workerId, Kind = kind, Amount = amount, Day = clock.Today, PayMonth = payMonth,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
        await repo.InsertPayEntryAsync(entry, ct);
        await outbox.AppendAsync("PayEntry", new PayEntryRecorded(entry.Id, clock.Now, workerId, (int)kind, amount.Value), ct);
    }
}

internal static class PayrollRegistration
{
    public static void AddPayroll(IServiceCollection services)
    {
        services.AddSingleton<IValidator<SaveWorkerCommand>, SaveWorkerValidator>();
        services.AddSingleton<IValidator<GiveAdvanceCommand>, GiveAdvanceValidator>();
        services.AddSingleton<PayMonthValidator>();
        services.AddSingleton<IValidator<GetPayslipQuery>>(sp => sp.GetRequiredService<PayMonthValidator>());
        services.AddSingleton<IValidator<PaySalaryCommand>>(sp => sp.GetRequiredService<PayMonthValidator>());
        services.AddSingleton<IValidator<SharePayslipCommand>>(sp => sp.GetRequiredService<PayMonthValidator>());

        services.AddTransient<WorkerHandlers>();
        services.AddTransient<IRequestHandler<SaveWorkerCommand, Result<Guid>>>(sp => sp.GetRequiredService<WorkerHandlers>());
        services.AddTransient<IRequestHandler<ListWorkersQuery, Result<IReadOnlyList<Worker>>>>(sp => sp.GetRequiredService<WorkerHandlers>());
        services.AddTransient<IRequestHandler<MarkAttendanceCommand, Result>>(sp => sp.GetRequiredService<WorkerHandlers>());
        services.AddTransient<IRequestHandler<GetAttendanceQuery, Result<IReadOnlyList<AttendanceEntry>>>>(sp => sp.GetRequiredService<WorkerHandlers>());

        services.AddTransient<PayHandlers>();
        services.AddTransient<IRequestHandler<GiveAdvanceCommand, Result>>(sp => sp.GetRequiredService<PayHandlers>());
        services.AddTransient<IRequestHandler<GetPayslipQuery, Result<Payslip>>>(sp => sp.GetRequiredService<PayHandlers>());
        services.AddTransient<IRequestHandler<PaySalaryCommand, Result<Payslip>>>(sp => sp.GetRequiredService<PayHandlers>());
        services.AddTransient<IRequestHandler<SharePayslipCommand, Result>>(sp => sp.GetRequiredService<PayHandlers>());
    }
}
