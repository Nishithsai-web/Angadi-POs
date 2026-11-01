namespace Angadi.Domain.Events;

// v2-stock outbox events (suppliers, purchases, orders, batch adjustments). Additive; type names are stable contracts.
public sealed record SupplierChanged(Guid AggregateId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record PurchaseRecorded(Guid AggregateId, DateTimeOffset OccurredAt, Guid SupplierId, long TotalPaise, int LineCount) : IDomainEvent;
public sealed record PurchaseOrderChanged(Guid AggregateId, DateTimeOffset OccurredAt, int Status) : IDomainEvent;
public sealed record BatchStockAdjusted(Guid AggregateId, DateTimeOffset OccurredAt, Guid BatchId, long DeltaMilli, int Reason) : IDomainEvent;
