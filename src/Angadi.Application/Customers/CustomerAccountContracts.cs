using Angadi.Application.Common;
using Angadi.Application.Reports;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Application.Customers;

/// <summary>The customer's previous bills (receipts), newest first. Tap one to view or resend it.</summary>
public sealed record GetCustomerBillsQuery(Guid CustomerId, int Limit = 100) : IQuery<IReadOnlyList<BillSummary>>;

/// <summary>Everything the owner wants on one screen about one customer.</summary>
public sealed record GetCustomerReportQuery(Guid CustomerId) : IQuery<CustomerReport?>;

public sealed record CustomerReport(
    Guid CustomerId,
    int BillCount, Paise TotalSpent, Paise AverageBill,
    DateTimeOffset? FirstVisit, DateTimeOffset? LastVisit,
    Paise PaidCash, Paise PaidUpi, Paise OnUdhaar,
    Paise DuesBalance, Paise DuesPaidSoFar,
    /// <summary>Days since the oldest udhaar that is still unpaid (payments settle the oldest first). Null when nothing is owed.</summary>
    int? OldestUnpaidDays,
    DateOnly? PromisedPayDate,
    IReadOnlyList<CustomerTopItem> TopItems,
    /// <summary>SEC-5: udhaar forgiven as write-offs (all time). Never part of DuesPaidSoFar — no money arrived.</summary>
    Paise DuesWrittenOffSoFar = default);

public sealed record CustomerTopItem(Guid ItemId, string Name, Unit Unit, Qty Qty, Paise Value, int Times);

/// <summary>Set or clear the date the customer promised to pay their udhaar/appu.</summary>
public sealed record SetPromisedPayDateCommand(Guid CustomerId, DateOnly? Date) : ICommand<Result>;

/// <summary>
/// SEC-5: forgive udhaar that will never be paid. The reason is mandatory and lands in the ledger entry's
/// Note — a write-off is NOT a payment: collections totals never count it, and the customer report shows
/// it on its own line (CustomerReport.DuesWrittenOffSoFar).
/// </summary>
public sealed record RecordDuesWriteOffCommand(Guid CustomerId, Paise Amount, string Reason) : ICommand<Result>;

/// <summary>Customers who owe money and are due a reminder today (promise date passed, or udhaar old enough).</summary>
public sealed record GetDueRemindersQuery : IQuery<IReadOnlyList<DueReminder>>;

public sealed record DueReminder(
    Guid CustomerId, string Name, string? Phone, Paise Balance, int OldestUnpaidDays,
    DateOnly? PromisedPayDate, DateTimeOffset? LastRemindedAt, DueReason Reason);

public enum DueReason { PromiseDatePassed = 0, UnpaidTooLong = 1 }
