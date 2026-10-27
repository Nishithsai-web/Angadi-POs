using Angadi.Domain.Billing;

namespace Angadi.Application.Persistence;

/// <summary>v2 returns: SalesReturn documents (header + lines). Insert-only — a recorded return is never edited.</summary>
public interface IReturnRepository
{
    Task<SalesReturn?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Every return of one bill, oldest first.</summary>
    Task<IReadOnlyList<SalesReturn>> ListByBillAsync(Guid billId, CancellationToken ct = default);
    /// <summary>Returns recorded on local days [from, to] (the return's own day — reports net them there).</summary>
    Task<IReadOnlyList<SalesReturn>> ListByDayAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    /// <summary>Atomically returns the next return number ("R-000001") and advances the sequence.</summary>
    Task<string> NextReturnNumberAsync(CancellationToken ct = default);
    Task SaveAsync(SalesReturn ret, CancellationToken ct = default);
}
