using Angadi.Domain.Common;
using Angadi.Domain.Payroll;

namespace Angadi.Application.Persistence;

/// <summary>Staff money: workers, daily attendance and the advance / recovery / salary ledger. Pay entries are insert-only.</summary>
public interface IPayrollRepository
{
    Task<Worker?> GetWorkerAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Worker>> ListWorkersAsync(bool includeInactive, CancellationToken ct = default);
    Task SaveWorkerAsync(Worker worker, CancellationToken ct = default);

    /// <summary>Sets (or replaces) the one mark for a worker on a day.</summary>
    Task SetAttendanceAsync(AttendanceEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AttendanceEntry>> GetAttendanceAsync(int year, int month, CancellationToken ct = default);

    Task InsertPayEntryAsync(PayEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<PayEntry>> ListPayEntriesAsync(Guid workerId, CancellationToken ct = default);
    /// <summary>Advances given minus recoveries, for one worker.</summary>
    Task<Paise> GetAdvanceBalanceAsync(Guid workerId, CancellationToken ct = default);
    Task<bool> IsSalaryPaidAsync(Guid workerId, int payMonth, CancellationToken ct = default);
}
