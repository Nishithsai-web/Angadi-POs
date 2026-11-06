namespace Angadi.Domain.Events;

// Staff-money outbox events. Additive; type names are stable contracts for the later sync server.
public sealed record WorkerChanged(Guid AggregateId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record AttendanceMarked(Guid AggregateId, DateTimeOffset OccurredAt, int Day, int Mark) : IDomainEvent;
public sealed record PayEntryRecorded(Guid AggregateId, DateTimeOffset OccurredAt, Guid WorkerId, int Kind, long AmountPaise) : IDomainEvent;
