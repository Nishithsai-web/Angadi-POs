using Angadi.Domain.Common;

namespace Angadi.Domain.Customers;

/// <summary>FR-21. Balance is denormalised; DuesEntry rows are the ledger (FR-22).</summary>
public sealed class Customer : Entity
{
    public string Name { get; set; } = "";
    public string? Phone { get; set; }   // E.164 without '+', e.g. 919876543210, for wa.me
    public Paise DuesBalance { get; set; }   // positive = customer owes the shop

    /// <summary>Customer account details the owner keeps (optional).</summary>
    public string? Address { get; set; }
    public string? Notes { get; set; }

    /// <summary>"Will pay on Sunday": the day the customer promised to clear udhaar/appu. Drives reminders.</summary>
    public DateOnly? PromisedPayDate { get; set; }

    /// <summary>When the last dues reminder was sent, so the same customer is not nagged every day.</summary>
    public DateTimeOffset? LastRemindedAt { get; set; }
}

/// <summary>Udhaar ledger. Charge from an udhaar bill, Payment when the customer pays, Reversal on void.</summary>
public sealed class DuesEntry : Entity
{
    public Guid CustomerId { get; set; }
    public DuesEntryKind Kind { get; set; }
    public Paise Amount { get; set; }   // always positive; Kind gives direction
    public Guid? BillId { get; set; }
    public string? Note { get; set; }
    /// <summary>H9-EXTENSION: UPI reference when a dues payment came by UPI.</summary>
    public string? UpiRef { get; set; }
}

/// <summary>Return (v2): goods returned against a bill reduce what the customer owes (never below zero).</summary>
public enum DuesEntryKind { Charge = 0, Payment = 1, Reversal = 2, Return = 3 }
