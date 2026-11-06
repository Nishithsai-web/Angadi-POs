using Angadi.Application.Common;
using Angadi.Domain.Common;
using Angadi.Domain.Payroll;

namespace Angadi.Application.Payroll;

public sealed record SaveWorkerCommand(Worker Worker) : ICommand<Result<Guid>>;
public sealed record ListWorkersQuery(bool IncludeInactive = false) : IQuery<Result<IReadOnlyList<Worker>>>;

/// <summary>One mark per worker per day; marking again replaces it. A future day is refused.</summary>
public sealed record MarkAttendanceCommand(Guid WorkerId, DateOnly Day, AttendanceMark Mark) : ICommand<Result>;
public sealed record GetAttendanceQuery(int Year, int Month) : IQuery<Result<IReadOnlyList<AttendanceEntry>>>;

public sealed record GiveAdvanceCommand(Guid WorkerId, Paise Amount, string? Note) : ICommand<Result>;

/// <summary>What the worker has earned this month and what is due, without recording anything.</summary>
public sealed record GetPayslipQuery(Guid WorkerId, int Year, int Month) : IQuery<Result<Payslip>>;

/// <summary>Records the recovery (if any) and the salary paid, once per worker per month, in one transaction.</summary>
public sealed record PaySalaryCommand(Guid WorkerId, int Year, int Month) : ICommand<Result<Payslip>>;

/// <summary>Sends the payslip text to the worker's WhatsApp (or the share sheet when they have no number).</summary>
public sealed record SharePayslipCommand(Guid WorkerId, int Year, int Month) : ICommand<Result>;
