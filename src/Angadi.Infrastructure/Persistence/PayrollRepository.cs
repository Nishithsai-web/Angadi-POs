using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Common;
using Angadi.Domain.Payroll;
using SQLite;
using static Angadi.Infrastructure.Persistence.Mapping;

namespace Angadi.Infrastructure.Persistence;

// Staff money (schema 7, docs/DB_SCHEMA.md). Additive tables only.

[Table("Worker")]
internal sealed class WorkerRow
{
    [PrimaryKey] public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public int PayType { get; set; }
    public long RatePaise { get; set; }
    public bool IsActive { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
}

/// <summary>One mark per worker per day: the key is the pair, so marking again replaces it.</summary>
[Table("Attendance")]
internal sealed class AttendanceRow
{
    [PrimaryKey] public string Key { get; set; } = "";
    [Indexed] public Guid WorkerId { get; set; }
    [Indexed] public int Day { get; set; }   // yyyymmdd
    public int Mark { get; set; }
}

[Table("PayEntry")]
internal sealed class PayEntryRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid WorkerId { get; set; }
    public int Kind { get; set; }
    public long AmountPaise { get; set; }
    public int Day { get; set; }
    public int PayMonth { get; set; }
    public string? Note { get; set; }
    public long CreatedAtMs { get; set; }
}

internal sealed class PayrollRepository(AngadiDb db, IClock clock) : IPayrollRepository
{
    public Task<Worker?> GetWorkerAsync(Guid id, CancellationToken ct = default)
        => db.ReadAsync(c => c.Find<WorkerRow>(id) is { } r ? ToDomain(r) : null, ct);

    public Task<IReadOnlyList<Worker>> ListWorkersAsync(bool includeInactive, CancellationToken ct = default)
        => db.ReadAsync<IReadOnlyList<Worker>>(c => c.Query<WorkerRow>(
                includeInactive ? "SELECT * FROM Worker ORDER BY Name" : "SELECT * FROM Worker WHERE IsActive = 1 ORDER BY Name")
            .Select(ToDomain).ToList(), ct);

    public Task SaveWorkerAsync(Worker worker, CancellationToken ct = default)
    {
        var now = clock.Now;
        if (worker.CreatedAt == default) worker.CreatedAt = now;
        worker.UpdatedAt = now;
        var row = new WorkerRow
        {
            Id = worker.Id, Name = worker.Name, Phone = worker.Phone, PayType = (int)worker.PayType, RatePaise = worker.Rate.Value,
            IsActive = worker.IsActive, CreatedAtMs = Ms(worker.CreatedAt), UpdatedAtMs = Ms(worker.UpdatedAt),
        };
        return db.RunAsync(c => c.InsertOrReplace(row), ct);
    }

    public Task SetAttendanceAsync(AttendanceEntry entry, CancellationToken ct = default)
    {
        var day = Day(entry.Day);
        var row = new AttendanceRow { Key = entry.WorkerId.ToString("N") + day, WorkerId = entry.WorkerId, Day = day, Mark = (int)entry.Mark };
        return db.RunAsync(c => c.InsertOrReplace(row), ct);
    }

    public Task<IReadOnlyList<AttendanceEntry>> GetAttendanceAsync(int year, int month, CancellationToken ct = default)
    {
        var from = year * 10000 + month * 100 + 1;
        var to = year * 10000 + month * 100 + 31;
        return db.ReadAsync<IReadOnlyList<AttendanceEntry>>(c => c.Query<AttendanceRow>(
                "SELECT * FROM Attendance WHERE Day >= ? AND Day <= ?", from, to)
            .Select(r => new AttendanceEntry(r.WorkerId, FromDay(r.Day), (AttendanceMark)r.Mark)).ToList(), ct);
    }

    public Task InsertPayEntryAsync(PayEntry entry, CancellationToken ct = default)
    {
        entry.CreatedAt = clock.Now;
        entry.UpdatedAt = entry.CreatedAt;
        var row = new PayEntryRow
        {
            Id = entry.Id, WorkerId = entry.WorkerId, Kind = (int)entry.Kind, AmountPaise = entry.Amount.Value, Day = Day(entry.Day),
            PayMonth = entry.PayMonth, Note = entry.Note, CreatedAtMs = Ms(entry.CreatedAt),
        };
        return db.RunAsync(c => c.Insert(row), ct);
    }

    public Task<IReadOnlyList<PayEntry>> ListPayEntriesAsync(Guid workerId, CancellationToken ct = default)
        => db.ReadAsync<IReadOnlyList<PayEntry>>(c => c.Query<PayEntryRow>(
                "SELECT * FROM PayEntry WHERE WorkerId = ? ORDER BY CreatedAtMs DESC", workerId)
            .Select(r => new PayEntry
            {
                Id = r.Id, WorkerId = r.WorkerId, Kind = (PayEntryKind)r.Kind, Amount = new Paise(r.AmountPaise), Day = FromDay(r.Day),
                PayMonth = r.PayMonth, Note = r.Note, CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.CreatedAtMs),
            }).ToList(), ct);

    public Task<Paise> GetAdvanceBalanceAsync(Guid workerId, CancellationToken ct = default)
        => db.ReadAsync(c => new Paise(c.ExecuteScalar<long>(
            "SELECT COALESCE(SUM(CASE Kind WHEN 0 THEN AmountPaise WHEN 1 THEN -AmountPaise ELSE 0 END), 0) FROM PayEntry WHERE WorkerId = ?", workerId)), ct);

    public Task<bool> IsSalaryPaidAsync(Guid workerId, int payMonth, CancellationToken ct = default)
        => db.ReadAsync(c => c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM PayEntry WHERE WorkerId = ? AND Kind = 2 AND PayMonth = ?", workerId, payMonth) > 0, ct);

    private static Worker ToDomain(WorkerRow r) => new()
    {
        Id = r.Id, Name = r.Name, Phone = r.Phone, PayType = (PayType)r.PayType, Rate = new Paise(r.RatePaise), IsActive = r.IsActive,
        CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.UpdatedAtMs),
    };
}
