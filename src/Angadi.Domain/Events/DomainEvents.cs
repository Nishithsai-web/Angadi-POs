namespace Angadi.Domain.Events;

/// <summary>Also the Outbox payload type: AggregateType + AggregateId + JSON of the event.</summary>
public interface IDomainEvent
{
    Guid AggregateId { get; }
    DateTimeOffset OccurredAt { get; }
}

public sealed record BillFinalized(Guid AggregateId, DateTimeOffset OccurredAt, long GrandTotalPaise) : IDomainEvent;
public sealed record BillVoided(Guid AggregateId, DateTimeOffset OccurredAt, string Reason) : IDomainEvent;
public sealed record StockAdjusted(Guid AggregateId, DateTimeOffset OccurredAt, long DeltaMilli, int Reason) : IDomainEvent;
public sealed record DuesPaymentRecorded(Guid AggregateId, DateTimeOffset OccurredAt, long AmountPaise) : IDomainEvent;
/// <summary>SEC-5: bad debt forgiven — kept distinct from DuesPaymentRecorded so collections never count it.</summary>
public sealed record DuesWrittenOff(Guid AggregateId, DateTimeOffset OccurredAt, long AmountPaise) : IDomainEvent;
// FIX(SEC-3, 2026-10-20, FIX-4): Flaw: ItemChanged carried no values, so an item price edit left no trace
// anywhere (price-shrinkage: staff lowers a price, sells to an accomplice, restores it — invisible).
// Fix: the payload carries the price before/after the save (old price null = newly created item).
// Why: the Outbox rides inside every backup, so the audit trail survives device loss; the fields are
// additive optionals, so payloads written before this change still deserialize. Logged in docs/AGENT_FIX_LOG.md.
public sealed record ItemChanged(Guid AggregateId, DateTimeOffset OccurredAt, long? OldPricePaise = null, long? NewPricePaise = null) : IDomainEvent;
public sealed record CustomerChanged(Guid AggregateId, DateTimeOffset OccurredAt) : IDomainEvent;
// FIX(SEC-3, 2026-10-20, FIX-4): Flaw: ShopProfileChanged carried no values — a swapped UPI ID (customer
// payments silently redirected to someone else's account) or a GSTIN change left no record of when or to
// what it changed. Fix: the payload carries old→new UPI ID and GSTIN on every profile save. Why: same as
// ItemChanged above — the trail must live in the Outbox/backup, not in a screen nobody can audit later.
// Logged in docs/AGENT_FIX_LOG.md.
public sealed record ShopProfileChanged(Guid AggregateId, DateTimeOffset OccurredAt, string? OldUpiId = null, string? NewUpiId = null, string? OldGstin = null, string? NewGstin = null) : IDomainEvent;
public sealed record ItemsSeeded(Guid AggregateId, DateTimeOffset OccurredAt, int Count) : IDomainEvent;
/// <summary>Customer-aggregate dues move (ARCHITECT_CORRECTIONS data medium): v2 can rebuild a ledger from these.</summary>
public sealed record NoteChanged(Guid AggregateId, DateTimeOffset OccurredAt, bool Deleted) : IDomainEvent;
public sealed record DuesCharged(Guid AggregateId, DateTimeOffset OccurredAt, long AmountPaise, Guid BillId) : IDomainEvent;
