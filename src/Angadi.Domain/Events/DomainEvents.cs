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
public sealed record ItemChanged(Guid AggregateId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record CustomerChanged(Guid AggregateId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record ShopProfileChanged(Guid AggregateId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record ItemsSeeded(Guid AggregateId, DateTimeOffset OccurredAt, int Count) : IDomainEvent;
/// <summary>Customer-aggregate dues move (ARCHITECT_CORRECTIONS data medium): v2 can rebuild a ledger from these.</summary>
public sealed record DuesCharged(Guid AggregateId, DateTimeOffset OccurredAt, long AmountPaise, Guid BillId) : IDomainEvent;
