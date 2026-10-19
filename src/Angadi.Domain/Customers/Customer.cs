using Angadi.Domain.Common;

namespace Angadi.Domain.Customers;

/// <summary>FR-21. Balance is denormalised; DuesEntry rows are the ledger (FR-22).</summary>
public sealed class Customer : Entity
{
    public string Name { get; set; } = "";
    public string? Phone { get; set; }   // E.164 without '+', e.g. 919876543210, for wa.me
    public Paise DuesBalance { get; set; }   // positive = customer owes the shop
}

/// <summary>Udhaar ledger. Charge from an udhaar bill, Payment when the customer pays, Reversal on void.</summary>
public sealed class DuesEntry : Entity
{
    public Guid CustomerId { get; set; }
    public DuesEntryKind Kind { get; set; }
    public Paise Amount { get; set; }   // always positive; Kind gives direction
    public Guid? BillId { get; set; }
    public string? Note { get; set; }
}

public enum DuesEntryKind { Charge = 0, Payment = 1, Reversal = 2 }
