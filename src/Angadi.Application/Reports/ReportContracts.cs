using Angadi.Application.Common;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Application.Reports;

/// <summary>FR-27.</summary>
public sealed record GetDayEndReportQuery(DateOnly Day) : IQuery<DayEndReport>;
public sealed record DayEndReport(
    DateOnly Day, int BillCount, int VoidCount, Paise SalesTotal,
    Paise Cash, Paise Upi, Paise Udhaar, Paise Discounts, Paise Tax);

/// <summary>FR-28.</summary>
public sealed record GetGstSummaryQuery(DateOnly From, DateOnly To) : IQuery<IReadOnlyList<TaxSplit>>;

/// <summary>FR-29.</summary>
public sealed record GetTopItemsQuery(DateOnly From, DateOnly To, int Limit = 10) : IQuery<IReadOnlyList<TopItem>>;
public sealed record TopItem(Guid ItemId, string DisplayName, Unit Unit, Qty QtySold, Paise Value);

/// <summary>FR-30.</summary>
public sealed record GetDuesOutstandingQuery : IQuery<DuesOutstanding>;
public sealed record DuesOutstanding(Paise Total, IReadOnlyList<CustomerDue> Customers);
public sealed record CustomerDue(Guid CustomerId, string Name, string? Phone, Paise Balance);

/// <summary>FR-20. Bill history list.</summary>
public sealed record ListBillsQuery(DateOnly From, DateOnly To) : IQuery<IReadOnlyList<BillSummary>>;
public sealed record BillSummary(Guid BillId, string Number, DateTimeOffset FinalizedAt, Paise GrandTotal, TenderType Tender, BillStatus Status);
